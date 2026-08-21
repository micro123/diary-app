from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass
from pathlib import Path

from .archive import validate_and_index
from .config import SUPPORTED_VARIANTS
from .repository import UpdateRepository, compact_json


SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
VERSION_ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
DATA_VERSION_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")
CHANNEL_PATTERN = re.compile(r"^[a-z0-9][a-z0-9-]{0,31}$")


class PublishConflictError(ValueError):
    pass


@dataclass(frozen=True)
class PackagePublishRequest:
    channel: str
    sequence: int
    version_id: str
    data_version: str
    rid: str
    flavor: str
    package_size: int
    package_sha256: str
    min_updater_version: int = 1
    min_incremental_sequence: int = 0
    manifest_format_version: int = 1


@dataclass(frozen=True)
class PackagePublishResult:
    published: bool
    envelope: dict[str, object]


def validate_publish_request(request: PackagePublishRequest) -> None:
    if CHANNEL_PATTERN.fullmatch(request.channel) is None:
        raise ValueError("channel 非法。")
    if request.sequence < 0:
        raise ValueError("sequence 非法。")
    if VERSION_ID_PATTERN.fullmatch(request.version_id) is None:
        raise ValueError("versionId 非法。")
    if DATA_VERSION_PATTERN.fullmatch(request.data_version) is None:
        raise ValueError("dataVersion 非法。")
    if (request.rid, request.flavor) not in SUPPORTED_VARIANTS:
        raise ValueError(f"不支持的发布维度：{request.rid}/{request.flavor}")
    if request.package_size <= 0:
        raise ValueError("完整包大小非法。")
    if SHA256_PATTERN.fullmatch(request.package_sha256) is None:
        raise ValueError("完整包 SHA-256 非法。")
    if request.manifest_format_version != 1:
        raise ValueError("manifestFormatVersion 不受支持。")
    if request.min_updater_version < 1 or request.min_incremental_sequence < 0:
        raise ValueError("更新器版本约束非法。")


def publish_archive(
    repository: UpdateRepository,
    archive_path: Path,
    staged_blob_directory: Path,
    request: PackagePublishRequest,
) -> PackagePublishResult:
    validate_publish_request(request)
    if archive_path.stat().st_size != request.package_size:
        raise ValueError("完整包大小与声明不一致。")
    if file_sha256(archive_path) != request.package_sha256:
        raise ValueError("完整包 SHA-256 与声明不一致。")

    current = repository.read_latest(request.channel, request.rid, request.flavor)
    if current is not None:
        current_manifest = current.get("manifest")
        current_package = current.get("fullPackage")
        if not isinstance(current_manifest, dict) or not isinstance(current_package, dict):
            raise ValueError("当前 latest 快照格式损坏。")
        current_sequence = int(current_manifest.get("sequence", -1))
        if current_sequence > request.sequence:
            raise PublishConflictError(
                f"服务器已有更高 sequence：{current_sequence} > {request.sequence}。"
            )
        if current_sequence == request.sequence:
            if (
                current_manifest.get("versionId") == request.version_id
                and current_package.get("size") == request.package_size
                and current_package.get("sha256") == request.package_sha256
            ):
                return PackagePublishResult(False, current)
            raise PublishConflictError(
                f"sequence {request.sequence} 已发布且内容不可变。"
            )

    files = validate_and_index(archive_path, request.rid, request.flavor, staged_blob_directory)
    content_value = {"rid": request.rid, "flavor": request.flavor, "files": files}
    content_id = f"sha256:{hashlib.sha256(compact_json(content_value)).hexdigest()}"
    manifest = {
        "manifestFormatVersion": request.manifest_format_version,
        "versionId": request.version_id,
        "sequence": request.sequence,
        "dataVersion": request.data_version,
        "channel": request.channel,
        "rid": request.rid,
        "flavor": request.flavor,
        "minUpdaterVersion": request.min_updater_version,
        "minIncrementalSequence": request.min_incremental_sequence,
        "manifestContentId": content_id,
        "files": files,
    }
    envelope = {
        "manifest": manifest,
        "fullPackage": {"size": request.package_size, "sha256": request.package_sha256},
    }
    published = repository.publish(envelope, archive_path, staged_blob_directory)
    return PackagePublishResult(published, envelope)


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()
