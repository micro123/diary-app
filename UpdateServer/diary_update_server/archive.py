from __future__ import annotations

import hashlib
import re
import stat
import tempfile
import zipfile
from pathlib import Path, PurePosixPath


MAX_FILE_COUNT = 10_000
MAX_SINGLE_FILE_SIZE = 1024 * 1024 * 1024
MAX_TOTAL_SIZE = 4 * 1024 * 1024 * 1024
MAX_COMPRESSION_RATIO = 1000


def normalize_name(name: str) -> str:
    normalized = name.replace("\\", "/")
    if not normalized or normalized.startswith(("/", "//")) or re.match(r"^[A-Za-z]:", normalized):
        raise ValueError(f"ZIP 包含绝对路径或空路径：{name!r}")
    path = PurePosixPath(normalized)
    if any(part in ("", ".", "..") for part in path.parts):
        raise ValueError(f"ZIP 包含非法路径段：{name!r}")
    return normalized


def _component(path: str) -> str:
    if path == "Diary.Updater" or path == "Diary.Updater.exe":
        return "updater"
    if path.startswith("Diary.Script.Worker"):
        return "worker"
    return "app"


def validate_and_index(
    archive_path: Path,
    rid: str,
    flavor: str,
    blob_directory: Path,
) -> list[dict[str, object]]:
    if (rid, flavor) not in {
        ("win-x64", "standard"),
        ("win-x64", "python313"),
        ("linux-x64", "standard"),
    }:
        raise ValueError(f"不支持的发布维度：{rid}/{flavor}")

    files: list[dict[str, object]] = []
    names: list[str] = []
    total_size = 0
    blob_directory.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive_path) as archive:
        infos = [info for info in archive.infolist() if not info.is_dir()]
        if not infos or len(infos) > MAX_FILE_COUNT:
            raise ValueError(f"ZIP 文件数量非法：{len(infos)}")
        for info in infos:
            name = normalize_name(info.filename)
            names.append(name)
            total_size += info.file_size
            if total_size > MAX_TOTAL_SIZE:
                raise ValueError(f"ZIP 解压后总大小超过上限：{total_size}")
            if info.file_size > MAX_SINGLE_FILE_SIZE:
                raise ValueError(f"ZIP 单文件超过大小上限：{name}")
            if info.compress_size > 0 and info.file_size / info.compress_size > MAX_COMPRESSION_RATIO:
                raise ValueError(f"ZIP 条目压缩比异常：{name}")
            unix_mode = (info.external_attr >> 16) & 0xFFFF
            if stat.S_ISLNK(unix_mode):
                raise ValueError(f"ZIP 包含符号链接：{name}")
            if info.create_system == 0 and info.external_attr & 0x400:
                raise ValueError(f"ZIP 包含 Windows 重解析点：{name}")
            digest = hashlib.sha256()
            with tempfile.NamedTemporaryFile(prefix="blob-", suffix=".tmp", dir=blob_directory, delete=False) as target:
                temporary_blob = Path(target.name)
                with archive.open(info) as source:
                    while block := source.read(1024 * 1024):
                        digest.update(block)
                        target.write(block)
            sha256 = digest.hexdigest()
            final_blob = blob_directory / sha256
            if final_blob.exists():
                temporary_blob.unlink()
                if final_blob.stat().st_size != info.file_size:
                    raise ValueError(f"内容缓存大小冲突：{sha256}")
            else:
                temporary_blob.replace(final_blob)
            files.append(
                {
                    "path": name,
                    "size": info.file_size,
                    "sha256": sha256,
                    "component": _component(name),
                    "executable": rid == "linux-x64" and bool(unix_mode & 0o111),
                }
            )
    if len(names) != len(set(names)):
        raise ValueError("ZIP 包含重复路径。")
    if rid == "win-x64" and len(names) != len({name.casefold() for name in names}):
        raise ValueError("Windows ZIP 包含仅大小写不同的冲突路径。")

    name_set = set(names)
    app_entry = "Diary.App.exe" if rid == "win-x64" else "Diary.App"
    worker_entry = "Diary.Script.Worker.exe" if rid == "win-x64" else "Diary.Script.Worker"
    updater_entry = "Diary.Updater.exe" if rid == "win-x64" else "Diary.Updater"
    mcp_entry = "Diary.Mcp.exe" if rid == "win-x64" else "Diary.Mcp"
    runtime_markers = ("coreclr.dll", "hostfxr.dll") if rid == "win-x64" else ("libcoreclr.so", "libhostfxr.so")
    required = {
        "Diary.App.dll",
        "Diary.Script.Worker.dll",
        "Diary.Mcp.dll",
        "Diary.Mcp.deps.json",
        "Diary.Mcp.runtimeconfig.json",
        app_entry,
        worker_entry,
        updater_entry,
        mcp_entry,
        *runtime_markers,
    }
    missing = sorted(required - name_set)
    if missing:
        raise ValueError(f"ZIP 缺少必需运行文件：{', '.join(missing)}")
    updater_sidecars = sorted(name for name in names if name.startswith("Diary.Updater.") and name != updater_entry)
    if updater_sidecars:
        raise ValueError(f"Diary.Updater 不是独立单文件：{', '.join(updater_sidecars)}")
    if any(name.casefold().endswith(".pdb") for name in names):
        raise ValueError("运行包包含 PDB。")
    if ".update/installed-manifest.json" in name_set:
        raise ValueError("源 ZIP 不能包含 .update/installed-manifest.json。")
    if any(name.casefold().endswith(".zip") and not name.startswith("python/") for name in names):
        raise ValueError("ZIP 包含未允许的嵌套归档。")
    runtime_roots = {name.split("/", 2)[1] for name in names if name.startswith("runtimes/") and name.count("/") >= 2}
    if runtime_roots != {rid, "any"}:
        raise ValueError(f"runtimes 目录不符合目标 RID：{sorted(runtime_roots)}")
    python_entries = [name for name in names if name.startswith("python/")]
    if flavor == "standard" and python_entries:
        raise ValueError("standard 包不能包含 python/ 目录。")
    if flavor == "python313":
        missing_python = sorted({"python/python.exe", "python/python313.dll", "python/python313.zip"} - name_set)
        if missing_python:
            raise ValueError(f"python313 包缺少运行时文件：{', '.join(missing_python)}")
    return sorted(files, key=lambda item: str(item["path"]))
