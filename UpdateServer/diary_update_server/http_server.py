from __future__ import annotations

import hashlib
import hmac
import html
import json
import logging
import re
import threading
import time
import urllib.parse
import uuid
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from .config import SUPPORTED_VARIANTS, ServerConfig
from .coordinator import SyncCoordinator
from .repository import UpdateRepository


LOGGER = logging.getLogger(__name__)
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
SEQUENCE_PATTERN = re.compile(r"^(0|[1-9][0-9]*)$")


class UpdateRequestHandler(BaseHTTPRequestHandler):
    repository: UpdateRepository
    config: ServerConfig
    coordinator: SyncCoordinator
    server_version = "DiaryUpdateServer/0.2"

    def log_message(self, message: str, *args: object) -> None:
        LOGGER.info("%s - %s", self.address_string(), message % args)

    def do_GET(self) -> None:
        request_id = uuid.uuid4().hex
        try:
            parsed = urllib.parse.urlparse(self.path)
            if parsed.path == "/":
                self.send_response(HTTPStatus.FOUND)
                self.send_header("Location", "/downloads")
                self.send_header("Content-Length", "0")
                self.end_headers()
                return
            if parsed.path == "/downloads":
                self._downloads_page()
                return
            if parsed.path == "/health/live":
                self._json(HTTPStatus.OK, {"status": "live"})
                return
            if parsed.path in ("/health/ready", "/health/status"):
                status = {**self.repository.status(), **self.coordinator.status()}
                code = HTTPStatus.OK if status["ready"] else HTTPStatus.SERVICE_UNAVAILABLE
                self._json(code, {"status": "ready" if status["ready"] else "notReady", **status})
                return
            if parsed.path == "/api/v1/updates/latest":
                self._latest(parsed.query, request_id)
                return
            if parsed.path.startswith("/api/v1/updates/content/"):
                self._content(parsed.path.rsplit("/", 1)[-1], request_id)
                return
            if parsed.path.startswith("/api/v1/updates/packages/"):
                self._package(parsed.path, request_id)
                return
            self._error(HTTPStatus.NOT_FOUND, "RESOURCE_NOT_FOUND", "resource not found", False, request_id)
        except (BrokenPipeError, ConnectionResetError):
            LOGGER.debug("客户端中断请求：%s", request_id)
        except Exception:
            LOGGER.exception("处理更新请求失败：%s", request_id)
            self._error(HTTPStatus.INTERNAL_SERVER_ERROR, "INTERNAL_ERROR", "internal server error", True, request_id)

    def do_POST(self) -> None:
        request_id = uuid.uuid4().hex
        try:
            parsed = urllib.parse.urlparse(self.path)
            if parsed.path != "/api/v1/internal/sync":
                self._error(HTTPStatus.NOT_FOUND, "RESOURCE_NOT_FOUND", "resource not found", False, request_id)
                return
            if not self._authorized_for_sync():
                self.send_response(HTTPStatus.UNAUTHORIZED)
                self.send_header("WWW-Authenticate", 'Bearer realm="diary-update-sync"')
                body = _json_bytes(
                    {"error": {"code": "UNAUTHORIZED", "message": "unauthorized", "retryable": False, "requestId": request_id}}
                )
                self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(body)))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                self.wfile.write(body)
                return
            if not self.coordinator.trigger_background("manual-api"):
                self._error(
                    HTTPStatus.CONFLICT,
                    "SYNC_IN_PROGRESS",
                    "an update synchronization is already running",
                    True,
                    request_id,
                )
                return
            self._json(
                HTTPStatus.ACCEPTED,
                {
                    "status": "accepted",
                    "message": "synchronization started without changing the automatic schedule",
                    "requestId": request_id,
                },
            )
        except (BrokenPipeError, ConnectionResetError):
            LOGGER.debug("客户端中断请求：%s", request_id)
        except Exception:
            LOGGER.exception("处理立即同步请求失败：%s", request_id)
            self._error(HTTPStatus.INTERNAL_SERVER_ERROR, "INTERNAL_ERROR", "internal server error", True, request_id)

    def _authorized_for_sync(self) -> bool:
        expected = self.config.sync_token
        if not expected:
            return True
        provided = self.headers.get("Authorization", "")
        prefix = "Bearer "
        return provided.startswith(prefix) and hmac.compare_digest(provided[len(prefix):], expected)

    def _downloads_page(self) -> None:
        latest = self.repository.list_latest()
        rows: list[str] = []
        for envelope in latest:
            manifest = envelope["manifest"]
            package = envelope["fullPackage"]
            channel = str(manifest["channel"])
            sequence = int(manifest["sequence"])
            rid = str(manifest["rid"])
            flavor = str(manifest["flavor"])
            package_url = _package_url(channel, sequence, rid, flavor)
            rows.append(
                "<tr>"
                f"<td><span class=\"channel {html.escape(channel)}\">{html.escape(channel)}</span></td>"
                f"<td>{html.escape(_rid_label(rid))}</td>"
                f"<td>{html.escape(_flavor_label(flavor))}</td>"
                f"<td><strong>{html.escape(str(manifest['versionId']))}</strong><small>sequence {sequence}</small></td>"
                f"<td>{html.escape(_format_size(int(package['size'])))}</td>"
                f"<td><code title=\"{html.escape(str(package['sha256']))}\">{html.escape(str(package['sha256'])[:12])}…</code></td>"
                f"<td><a class=\"download\" href=\"{html.escape(package_url)}\">下载完整包</a></td>"
                "</tr>"
            )
        if not rows:
            rows.append('<tr><td class="empty" colspan="7">服务器尚未同步到可下载版本，请稍后刷新。</td></tr>')
        sync_status = self.coordinator.status()
        status_text = _sync_status_text(sync_status)
        body = f"""<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <meta name="robots" content="noindex,nofollow">
  <title>DiaryApp 下载</title>
  <style>
    :root {{ color-scheme: light dark; font-family: system-ui,-apple-system,"Segoe UI",sans-serif; }}
    body {{ margin:0; background:#f3f5f8; color:#172033; }}
    main {{ width:min(1180px,calc(100% - 32px)); margin:48px auto; }}
    header {{ display:flex; align-items:end; justify-content:space-between; gap:20px; margin-bottom:20px; }}
    h1 {{ margin:0 0 8px; font-size:32px; }}
    p {{ margin:0; color:#5c667a; }}
    .refresh {{ color:#2457d6; text-decoration:none; font-weight:600; }}
    .panel {{ overflow:auto; background:#fff; border:1px solid #dfe3ea; border-radius:14px; box-shadow:0 8px 30px #24304a12; }}
    table {{ width:100%; border-collapse:collapse; min-width:900px; }}
    th,td {{ padding:16px; text-align:left; border-bottom:1px solid #edf0f4; vertical-align:middle; }}
    th {{ color:#6a7282; font-size:13px; font-weight:650; background:#fafbfc; }}
    tr:last-child td {{ border-bottom:0; }}
    small {{ display:block; margin-top:4px; color:#7a8497; }}
    code {{ font-size:12px; }}
    .channel {{ display:inline-block; padding:4px 9px; border-radius:999px; background:#e8eefc; color:#2457b8; }}
    .channel.preview {{ background:#fff0d9; color:#9a5b00; }}
    .download {{ display:inline-block; padding:9px 14px; border-radius:9px; background:#2457d6; color:#fff; text-decoration:none; white-space:nowrap; }}
    .download:hover {{ background:#1746bd; }}
    .empty {{ padding:42px; text-align:center; color:#7a8497; }}
    footer {{ margin-top:16px; color:#737d90; font-size:13px; }}
    @media (prefers-color-scheme:dark) {{
      body {{ background:#11151d; color:#edf1f7; }} .panel {{ background:#181e28; border-color:#303846; }}
      th {{ background:#1d2430; color:#aeb8ca; }} th,td {{ border-color:#2a3240; }} p,footer,small {{ color:#9aa5b8; }}
    }}
  </style>
</head>
<body>
  <main>
    <header><div><h1>DiaryApp 完整包下载</h1><p>仅展示服务器当前保留的 latest 版本。</p></div><a class="refresh" href="/downloads">刷新页面</a></header>
    <div class="panel"><table><thead><tr><th>频道</th><th>平台</th><th>包类型</th><th>版本</th><th>大小</th><th>SHA-256</th><th>操作</th></tr></thead><tbody>{''.join(rows)}</tbody></table></div>
    <footer>{html.escape(status_text)} · 自动同步周期 {self.config.poll_interval_seconds // 3600} 小时</footer>
  </main>
</body>
</html>"""
        self._html(HTTPStatus.OK, body)

    def _latest(self, query: str, request_id: str) -> None:
        values = urllib.parse.parse_qs(query, keep_blank_values=True)
        if set(values) != {"channel", "rid", "flavor"} or any(len(item) != 1 for item in values.values()):
            self._error(HTTPStatus.BAD_REQUEST, "INVALID_DIMENSION", "invalid update dimensions", False, request_id)
            return
        channel, rid, flavor = values["channel"][0], values["rid"][0], values["flavor"][0]
        if channel not in self.config.allowed_channels or (rid, flavor) not in SUPPORTED_VARIANTS:
            self._error(HTTPStatus.BAD_REQUEST, "INVALID_DIMENSION", "unsupported update dimensions", False, request_id)
            return
        envelope = self.repository.read_latest(channel, rid, flavor)
        if envelope is None:
            self._error(HTTPStatus.NOT_FOUND, "NO_LOCAL_SNAPSHOT", "no local snapshot", False, request_id)
            return
        self._json(HTTPStatus.OK, envelope)

    def _content(self, sha256: str, request_id: str) -> None:
        if not SHA256_PATTERN.fullmatch(sha256):
            self._error(HTTPStatus.BAD_REQUEST, "INVALID_DIMENSION", "invalid sha256", False, request_id)
            return
        path = self.repository.blob_path(sha256)
        if not path.is_file():
            self._error(HTTPStatus.NOT_FOUND, "BLOB_NOT_FOUND", "content blob not found", False, request_id)
            return
        if _file_sha256(path) != sha256:
            self._error(HTTPStatus.SERVICE_UNAVAILABLE, "SNAPSHOT_CORRUPT", "content blob is corrupt", True, request_id)
            return
        self._file(path, "application/octet-stream", sha256)

    def _package(self, path: str, request_id: str) -> None:
        parts = path.removeprefix("/api/v1/updates/packages/").split("/")
        if len(parts) != 4 or not SEQUENCE_PATTERN.fullmatch(parts[1]):
            self._error(HTTPStatus.BAD_REQUEST, "INVALID_DIMENSION", "invalid package dimensions", False, request_id)
            return
        channel, sequence_text, rid, flavor = parts
        if channel not in self.config.allowed_channels or (rid, flavor) not in SUPPORTED_VARIANTS:
            self._error(HTTPStatus.BAD_REQUEST, "INVALID_DIMENSION", "unsupported package dimensions", False, request_id)
            return
        sequence = int(sequence_text)
        snapshot = self.repository.snapshot_directory(channel, sequence, rid, flavor)
        manifest_path = snapshot / "manifest.json"
        package_path = snapshot / "package.zip"
        if not snapshot.exists():
            self._error(HTTPStatus.GONE, "PACKAGE_NOT_FOUND", "package snapshot is no longer retained", False, request_id)
            return
        if not manifest_path.is_file() or not package_path.is_file():
            self._error(HTTPStatus.SERVICE_UNAVAILABLE, "SNAPSHOT_CORRUPT", "package snapshot is corrupt", True, request_id)
            return
        envelope = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest = envelope["manifest"]
        descriptor = envelope["fullPackage"]
        if package_path.stat().st_size != int(descriptor["size"]) or _file_sha256(package_path) != descriptor["sha256"]:
            self._error(HTTPStatus.SERVICE_UNAVAILABLE, "SNAPSHOT_CORRUPT", "package snapshot is corrupt", True, request_id)
            return
        filename = _package_filename(str(manifest["versionId"]), rid, flavor)
        self._file(package_path, "application/zip", str(descriptor["sha256"]), filename=filename)

    def _json(self, status: HTTPStatus, value: object) -> None:
        body = _json_bytes(value)
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _html(self, status: HTTPStatus, value: str) -> None:
        body = value.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'")
        self.end_headers()
        self.wfile.write(body)

    def _error(self, status: HTTPStatus, code: str, message: str, retryable: bool, request_id: str) -> None:
        self._json(status, {"error": {"code": code, "message": message, "retryable": retryable, "requestId": request_id}})

    def _file(self, path: Path, content_type: str, etag: str, filename: str | None = None) -> None:
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(path.stat().st_size))
        self.send_header("ETag", f'"sha256:{etag}"')
        self.send_header("Cache-Control", "public, max-age=31536000, immutable")
        self.send_header("X-Content-Type-Options", "nosniff")
        if filename is not None:
            self.send_header("Content-Disposition", f'attachment; filename="{filename}"')
        self.end_headers()
        with path.open("rb") as stream:
            while block := stream.read(1024 * 1024):
                self.wfile.write(block)


def _json_bytes(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def _file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def _package_url(channel: str, sequence: int, rid: str, flavor: str) -> str:
    values = [channel, str(sequence), rid, flavor]
    return "/api/v1/updates/packages/" + "/".join(urllib.parse.quote(value, safe="") for value in values)


def _package_filename(version_id: str, rid: str, flavor: str) -> str:
    suffix = f"-{flavor}" if flavor != "standard" else ""
    raw = f"DiaryAppNG-{version_id}-{rid}{suffix}.zip"
    return re.sub(r"[^A-Za-z0-9._-]", "_", raw)


def _rid_label(rid: str) -> str:
    return {"win-x64": "Windows x64", "linux-x64": "Linux x64"}.get(rid, rid)


def _flavor_label(flavor: str) -> str:
    return {"standard": "标准版", "python313": "Python 3.13 版"}.get(flavor, flavor)


def _format_size(size: int) -> str:
    if size >= 1024 * 1024 * 1024:
        return f"{size / (1024 * 1024 * 1024):.2f} GiB"
    if size >= 1024 * 1024:
        return f"{size / (1024 * 1024):.1f} MiB"
    return f"{size / 1024:.1f} KiB"


def _sync_status_text(status: dict[str, object]) -> str:
    if status.get("syncState") == "running":
        return "正在同步 GitHub Release"
    if status.get("lastResult") == "success":
        return f"最近同步成功：{status.get('lastCompletedAt') or '未知时间'}"
    if status.get("lastResult") == "failed":
        return f"最近同步失败：{status.get('lastCompletedAt') or '未知时间'}"
    return "尚无同步记录"


def create_server(
    config: ServerConfig,
    repository: UpdateRepository,
    coordinator: SyncCoordinator | None = None,
) -> ThreadingHTTPServer:
    active_coordinator = coordinator or SyncCoordinator(config, repository)
    handler = type(
        "ConfiguredUpdateRequestHandler",
        (UpdateRequestHandler,),
        {"config": config, "repository": repository, "coordinator": active_coordinator},
    )
    return ThreadingHTTPServer((config.listen_host, config.listen_port), handler)


def start_polling(
    config: ServerConfig,
    coordinator: SyncCoordinator,
    stop: threading.Event,
    delay_first_sync: bool = False,
) -> threading.Thread:
    def worker() -> None:
        next_run = time.monotonic() + (config.poll_interval_seconds if delay_first_sync else 0)
        while not stop.is_set():
            delay = max(0.0, next_run - time.monotonic())
            if stop.wait(delay):
                return
            if not coordinator.synchronize("scheduled"):
                LOGGER.info("自动同步时间到达，但已有同步正在执行。")
            next_run += config.poll_interval_seconds
            now = time.monotonic()
            while next_run <= now:
                next_run += config.poll_interval_seconds

    thread = threading.Thread(target=worker, name="release-sync-scheduler", daemon=True)
    thread.start()
    return thread
