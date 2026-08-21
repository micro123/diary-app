from __future__ import annotations

import hashlib
import json
import logging
import re
import shutil
import tempfile
from pathlib import Path

from .archive import validate_and_index
from .config import SUPPORTED_VARIANTS, ServerConfig
from .github import GitHubReleaseClient
from .repository import UpdateRepository, compact_json


LOGGER = logging.getLogger(__name__)
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def validate_metadata(metadata: dict[str, object], repository: str, tag: str) -> None:
    required = {
        "schemaVersion", "repository", "tag", "commit", "versionId", "sequence",
        "dataVersion", "channel", "manifestFormatVersion", "minUpdaterVersion",
        "minIncrementalSequence", "assets", "debugAssets",
    }
    missing = sorted(required - metadata.keys())
    if missing:
        raise ValueError(f"metadata 缺少字段：{', '.join(missing)}")
    if metadata["schemaVersion"] != 1 or metadata["manifestFormatVersion"] != 1:
        raise ValueError("metadata 协议版本不受支持。")
    if metadata["repository"] != repository or metadata["tag"] != tag:
        raise ValueError("metadata 与 GitHub Release 身份不匹配。")
    if re.fullmatch(r"[0-9a-f]{40}", str(metadata["commit"])) is None:
        raise ValueError("metadata commit 非法。")
    if not isinstance(metadata["sequence"], int) or int(metadata["sequence"]) < 0:
        raise ValueError("metadata sequence 非法。")
    if not isinstance(metadata["assets"], list) or not isinstance(metadata["debugAssets"], list):
        raise ValueError("metadata 资产列表格式非法。")
    variants: set[tuple[str, str]] = set()
    for raw_asset in metadata["assets"]:
        if not isinstance(raw_asset, dict):
            raise ValueError("metadata 包含非法资产。")
        expected = {"rid", "flavor", "kind", "name", "size", "sha256"}
        if set(raw_asset) != expected or raw_asset["kind"] != "package":
            raise ValueError("metadata 运行资产字段非法。")
        variant = str(raw_asset["rid"]), str(raw_asset["flavor"])
        if variant not in SUPPORTED_VARIANTS or variant in variants:
            raise ValueError(f"metadata 发布维度非法或重复：{variant[0]}/{variant[1]}")
        variants.add(variant)
        if not isinstance(raw_asset["size"], int) or int(raw_asset["size"]) <= 0:
            raise ValueError("metadata 资产大小非法。")
        if not SHA256_PATTERN.fullmatch(str(raw_asset["sha256"])):
            raise ValueError("metadata 资产 SHA-256 非法。")
        if Path(str(raw_asset["name"])).name != raw_asset["name"]:
            raise ValueError("metadata 资产名称不能包含路径。")
    if variants != SUPPORTED_VARIANTS:
        raise ValueError(f"metadata 发布矩阵不完整：{sorted(variants)}")
    debug_rids: set[str] = set()
    for raw_debug_asset in metadata["debugAssets"]:
        if not isinstance(raw_debug_asset, dict) or set(raw_debug_asset) != {"rid", "name"}:
            raise ValueError("metadata 调试资产字段非法。")
        rid = str(raw_debug_asset["rid"])
        if rid not in {"win-x64", "linux-x64"} or rid in debug_rids:
            raise ValueError("metadata 调试资产 RID 非法或重复。")
        if Path(str(raw_debug_asset["name"])).name != raw_debug_asset["name"]:
            raise ValueError("metadata 调试资产名称不能包含路径。")
        debug_rids.add(rid)
    if debug_rids != {"win-x64", "linux-x64"}:
        raise ValueError("metadata 调试资产矩阵不完整。")


class ReleaseSynchronizer:
    def __init__(self, config: ServerConfig, repository: UpdateRepository, client: GitHubReleaseClient | None = None):
        self._config = config
        self._repository = repository
        self._client = client or GitHubReleaseClient(config)

    def synchronize(self) -> int:
        candidates: dict[tuple[str, str, str], tuple[dict[str, object], dict[str, object], dict[str, str]]] = {}
        for release in self._client.list_releases():
            if release.get("draft") is True:
                continue
            tag = str(release.get("tag_name", ""))
            assets = release.get("assets")
            if not tag or not isinstance(assets, list):
                continue
            by_name = {
                str(asset.get("name")): str(asset.get("browser_download_url"))
                for asset in assets
                if isinstance(asset, dict) and asset.get("name") and asset.get("browser_download_url")
            }
            metadata_name = f"DiaryAppNG-{tag}-release-metadata.json"
            metadata_url = by_name.get(metadata_name)
            if metadata_url is None:
                continue
            try:
                metadata = self._client.read_json(metadata_url)
                validate_metadata(metadata, self._config.repository, tag)
            except (OSError, ValueError, json.JSONDecodeError) as exception:
                LOGGER.warning("忽略无效 Release metadata：tag=%s, error=%s", tag, exception)
                continue
            channel = str(metadata["channel"])
            if channel not in self._config.allowed_channels:
                continue
            declared_names = {
                str(asset["name"])
                for group in (metadata["assets"], metadata["debugAssets"])
                for asset in group
                if isinstance(asset, dict)
            }
            missing_names = sorted(declared_names - by_name.keys())
            if missing_names:
                LOGGER.warning("忽略资产不完整的 Release：tag=%s, missing=%s", tag, ", ".join(missing_names))
                continue
            for package in metadata["assets"]:
                assert isinstance(package, dict)
                name = str(package["name"])
                download_url = by_name.get(name)
                assert download_url is not None
                key = channel, str(package["rid"]), str(package["flavor"])
                existing = candidates.get(key)
                if existing is None or int(existing[0]["sequence"]) < int(metadata["sequence"]):
                    candidates[key] = (metadata, package, {"url": download_url, "tag": tag})

        published = 0
        for metadata, package, source in candidates.values():
            if self._sync_package(metadata, package, source):
                published += 1
        removed_snapshots, removed_blobs = self._repository.prune_to_latest()
        if removed_snapshots or removed_blobs:
            LOGGER.info(
                "已清理旧更新数据：snapshots=%d, blobs=%d",
                removed_snapshots,
                removed_blobs,
            )
        return published

    def _sync_package(
        self,
        metadata: dict[str, object],
        package: dict[str, object],
        source: dict[str, str],
    ) -> bool:
        channel = str(metadata["channel"])
        sequence = int(metadata["sequence"])
        rid = str(package["rid"])
        flavor = str(package["flavor"])
        current = self._repository.read_latest(channel, rid, flavor)
        if current is not None:
            current_manifest = current.get("manifest")
            if isinstance(current_manifest, dict) and int(current_manifest.get("sequence", -1)) >= sequence:
                return False

        transaction = Path(tempfile.mkdtemp(prefix="sync-", dir=self._repository.transactions))
        try:
            archive_path = transaction / "package.zip"
            staged_blobs = transaction / "blobs"
            self._client.download(source["url"], archive_path)
            expected_size = int(package["size"])
            expected_sha256 = str(package["sha256"])
            if archive_path.stat().st_size != expected_size or file_sha256(archive_path) != expected_sha256:
                raise ValueError(f"Release 资产大小或 SHA-256 不匹配：{package['name']}")
            files = validate_and_index(archive_path, rid, flavor, staged_blobs)
            content_value = {"rid": rid, "flavor": flavor, "files": files}
            content_id = f"sha256:{hashlib.sha256(compact_json(content_value)).hexdigest()}"
            manifest = {
                "manifestFormatVersion": int(metadata["manifestFormatVersion"]),
                "versionId": str(metadata["versionId"]),
                "sequence": sequence,
                "dataVersion": str(metadata["dataVersion"]),
                "channel": channel,
                "rid": rid,
                "flavor": flavor,
                "minUpdaterVersion": int(metadata["minUpdaterVersion"]),
                "minIncrementalSequence": int(metadata["minIncrementalSequence"]),
                "manifestContentId": content_id,
                "files": files,
            }
            envelope = {
                "manifest": manifest,
                "fullPackage": {"size": expected_size, "sha256": expected_sha256},
            }
            published = self._repository.publish(envelope, archive_path, staged_blobs)
            if published:
                LOGGER.info("已发布更新快照：%s/%s/%s/%s", channel, sequence, rid, flavor)
            return published
        finally:
            shutil.rmtree(transaction, ignore_errors=True)
