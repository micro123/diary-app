from __future__ import annotations

import json
import os
import re
import shutil
import tempfile
from pathlib import Path


def compact_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def write_json_atomic(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        temporary.replace(path)
    except BaseException:
        temporary.unlink(missing_ok=True)
        raise


class UpdateRepository:
    def __init__(self, root: Path):
        self.root = root
        self.snapshots = root / "snapshots"
        self.latest = root / "latest"
        self.blobs = root / "blobs"
        self.transactions = root / "transactions"
        for directory in (self.snapshots, self.latest, self.blobs, self.transactions):
            directory.mkdir(parents=True, exist_ok=True)

    @staticmethod
    def _dimension_path(channel: str, sequence: int, rid: str, flavor: str) -> tuple[str, ...]:
        return channel, str(sequence), rid, flavor

    def snapshot_directory(self, channel: str, sequence: int, rid: str, flavor: str) -> Path:
        return self.snapshots.joinpath(*self._dimension_path(channel, sequence, rid, flavor))

    def latest_path(self, channel: str, rid: str, flavor: str) -> Path:
        return self.latest / channel / rid / f"{flavor}.json"

    def read_latest(self, channel: str, rid: str, flavor: str) -> dict[str, object] | None:
        path = self.latest_path(channel, rid, flavor)
        if not path.is_file():
            return None
        value = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(value, dict):
            raise ValueError("latest 索引格式损坏。")
        return value

    def list_latest(self) -> list[dict[str, object]]:
        values: list[dict[str, object]] = []
        for path in self.latest.glob("*/*/*.json"):
            value = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(value, dict) or not isinstance(value.get("manifest"), dict):
                raise ValueError(f"latest 索引格式损坏：{path.relative_to(self.latest)}")
            values.append(value)
        return sorted(
            values,
            key=lambda item: (
                str(item["manifest"].get("channel", "")),
                str(item["manifest"].get("rid", "")),
                str(item["manifest"].get("flavor", "")),
            ),
        )

    def publish(
        self,
        envelope: dict[str, object],
        package_source: Path,
        staged_blob_directory: Path,
    ) -> bool:
        manifest = envelope["manifest"]
        assert isinstance(manifest, dict)
        channel = str(manifest["channel"])
        sequence = int(manifest["sequence"])
        rid = str(manifest["rid"])
        flavor = str(manifest["flavor"])
        current = self.read_latest(channel, rid, flavor)
        if current is not None:
            current_manifest = current.get("manifest")
            if isinstance(current_manifest, dict) and int(current_manifest.get("sequence", -1)) > sequence:
                return False

        snapshot = self.snapshot_directory(channel, sequence, rid, flavor)
        snapshot.mkdir(parents=True, exist_ok=True)
        package_target = snapshot / "package.zip"
        manifest_target = snapshot / "manifest.json"
        if manifest_target.exists():
            existing = json.loads(manifest_target.read_text(encoding="utf-8"))
            if existing != envelope:
                raise ValueError(f"同一发布维度的快照内容不可变：{channel}/{sequence}/{rid}/{flavor}")
            if not package_target.is_file():
                raise ValueError(f"已有快照缺少完整包：{channel}/{sequence}/{rid}/{flavor}")
            package_source.unlink()
        else:
            if package_target.exists():
                raise ValueError(f"已有快照不完整：{channel}/{sequence}/{rid}/{flavor}")
            shutil.move(str(package_source), package_target)
        for staged_blob in staged_blob_directory.iterdir():
            if not staged_blob.is_file():
                continue
            target = self.blobs / staged_blob.name
            if target.exists():
                staged_blob.unlink()
            else:
                staged_blob.replace(target)
        if not manifest_target.exists():
            write_json_atomic(manifest_target, envelope)
        write_json_atomic(self.latest_path(channel, rid, flavor), envelope)
        return True

    def package_path(self, channel: str, sequence: int, rid: str, flavor: str) -> Path:
        return self.snapshot_directory(channel, sequence, rid, flavor) / "package.zip"

    def blob_path(self, sha256: str) -> Path:
        return self.blobs / sha256

    def prune_to_latest(self) -> tuple[int, int]:
        retained_snapshots: set[Path] = set()
        retained_blobs: set[str] = set()
        for latest_path in self.latest.glob("*/*/*.json"):
            relative = latest_path.relative_to(self.latest)
            channel, rid, flavor_file = relative.parts
            flavor = Path(flavor_file).stem
            envelope = json.loads(latest_path.read_text(encoding="utf-8"))
            manifest = envelope.get("manifest") if isinstance(envelope, dict) else None
            if not isinstance(manifest, dict):
                raise ValueError(f"latest 索引格式损坏：{relative}")
            if (
                manifest.get("channel") != channel
                or manifest.get("rid") != rid
                or manifest.get("flavor") != flavor
                or not isinstance(manifest.get("sequence"), int)
            ):
                raise ValueError(f"latest 索引维度不匹配：{relative}")
            snapshot = self.snapshot_directory(channel, int(manifest["sequence"]), rid, flavor)
            if not (snapshot / "manifest.json").is_file() or not (snapshot / "package.zip").is_file():
                raise ValueError(f"latest 引用的快照不完整：{relative}")
            retained_snapshots.add(snapshot.resolve())
            files = manifest.get("files")
            if not isinstance(files, list):
                raise ValueError(f"latest 文件清单格式损坏：{relative}")
            for file in files:
                sha256 = file.get("sha256") if isinstance(file, dict) else None
                if not isinstance(sha256, str) or re.fullmatch(r"[0-9a-f]{64}", sha256) is None:
                    raise ValueError(f"latest 文件 SHA-256 非法：{relative}")
                retained_blobs.add(sha256)

        removed_snapshots = 0
        for snapshot in list(self.snapshots.glob("*/*/*/*")):
            if snapshot.is_dir() and snapshot.resolve() not in retained_snapshots:
                shutil.rmtree(snapshot)
                removed_snapshots += 1
        for directory, _, _ in os.walk(self.snapshots, topdown=False):
            candidate = Path(directory)
            if candidate != self.snapshots:
                try:
                    candidate.rmdir()
                except OSError:
                    pass

        removed_blobs = 0
        for blob in self.blobs.iterdir():
            if (
                blob.is_file()
                and re.fullmatch(r"[0-9a-f]{64}", blob.name) is not None
                and blob.name not in retained_blobs
            ):
                blob.unlink()
                removed_blobs += 1
        return removed_snapshots, removed_blobs

    def status(self) -> dict[str, object]:
        latest_count = sum(1 for path in self.latest.rglob("*.json") if path.is_file())
        return {"ready": os.access(self.root, os.R_OK | os.W_OK), "latestCount": latest_count}
