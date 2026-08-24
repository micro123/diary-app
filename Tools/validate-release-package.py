#!/usr/bin/env python3

from __future__ import annotations

import argparse
import re
import stat
import sys
import zipfile
from pathlib import PurePosixPath


MAX_FILE_COUNT = 10_000
MAX_SINGLE_FILE_SIZE = 1024 * 1024 * 1024
MAX_TOTAL_SIZE = 4 * 1024 * 1024 * 1024
MAX_COMPRESSION_RATIO = 1000


def configure_standard_streams() -> None:
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is not None:
            reconfigure(encoding="utf-8", errors="backslashreplace")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="校验 DiaryApp Release ZIP 的安全性和发布契约。")
    parser.add_argument("--archive", required=True)
    parser.add_argument("--rid", required=True, choices=("win-x64", "linux-x64"))
    parser.add_argument("--flavor", required=True, choices=("standard", "python313"))
    parser.add_argument(
        "--require-user-manual",
        action="store_true",
        help="要求 ZIP 包含发布版内置 HTML/PDF 用户手册。",
    )
    return parser.parse_args()


def fail(message: str) -> None:
    raise ValueError(message)


def normalized_name(name: str) -> str:
    normalized = name.replace("\\", "/")
    if not normalized or normalized.startswith(("/", "//")) or re.match(r"^[A-Za-z]:", normalized):
        fail(f"ZIP 包含绝对路径或空路径：{name!r}")
    path = PurePosixPath(normalized)
    if any(part in ("", ".", "..") for part in path.parts):
        fail(f"ZIP 包含非法路径段：{name!r}")
    return normalized


def validate() -> None:
    args = parse_args()
    if args.flavor == "python313" and args.rid != "win-x64":
        fail("python313 flavor 只允许用于 win-x64。")

    with zipfile.ZipFile(args.archive) as archive:
        infos = [info for info in archive.infolist() if not info.is_dir()]
        if not infos or len(infos) > MAX_FILE_COUNT:
            fail(f"ZIP 文件数量非法：{len(infos)}")
        names: list[str] = []
        total_size = 0
        for info in infos:
            name = normalized_name(info.filename)
            names.append(name)
            total_size += info.file_size
            if info.file_size > MAX_SINGLE_FILE_SIZE:
                fail(f"ZIP 单文件超过大小上限：{name}")
            if info.compress_size > 0 and info.file_size / info.compress_size > MAX_COMPRESSION_RATIO:
                fail(f"ZIP 条目压缩比异常：{name}")
            unix_mode = (info.external_attr >> 16) & 0xFFFF
            if stat.S_ISLNK(unix_mode):
                fail(f"ZIP 包含符号链接：{name}")
            if info.create_system == 0 and info.external_attr & 0x400:
                fail(f"ZIP 包含 Windows 重解析点：{name}")
        if total_size > MAX_TOTAL_SIZE:
            fail(f"ZIP 解压后总大小超过上限：{total_size}")

    if len(names) != len(set(names)):
        fail("ZIP 包含重复路径。")
    if args.rid == "win-x64" and len(names) != len({name.casefold() for name in names}):
        fail("Windows ZIP 包含仅大小写不同的冲突路径。")

    name_set = set(names)
    app_entry = "Diary.App.exe" if args.rid == "win-x64" else "Diary.App"
    worker_entry = "Diary.Script.Worker.exe" if args.rid == "win-x64" else "Diary.Script.Worker"
    updater_entry = "Diary.Updater.exe" if args.rid == "win-x64" else "Diary.Updater"
    mcp_entry = "Diary.Mcp.exe" if args.rid == "win-x64" else "Diary.Mcp"
    runtime_markers = (
        ("coreclr.dll", "hostfxr.dll")
        if args.rid == "win-x64"
        else ("libcoreclr.so", "libhostfxr.so")
    )
    required = {
        "Diary.App.dll",
        "Diary.Script.Worker.dll",
        app_entry,
        worker_entry,
        updater_entry,
        mcp_entry,
        "Diary.Mcp.dll",
        "Diary.Mcp.deps.json",
        "Diary.Mcp.runtimeconfig.json",
        *runtime_markers,
    }
    if args.require_user_manual:
        required.update(
            {
                "Docs/UserManual/DiaryApp-User-Manual.html",
                "Docs/UserManual/DiaryApp-User-Manual.pdf",
            }
        )
    missing = sorted(required - name_set)
    if missing:
        fail(f"ZIP 缺少必需运行文件：{', '.join(missing)}")

    updater_sidecars = sorted(
        name
        for name in names
        if name.startswith("Diary.Updater.") and name != updater_entry
    )
    if updater_sidecars:
        fail(f"Diary.Updater 不是独立单文件：{', '.join(updater_sidecars)}")
    pdb_entries = sorted(name for name in names if name.casefold().endswith(".pdb"))
    if pdb_entries:
        fail(f"运行包包含 PDB：{pdb_entries[0]}")
    debug_ui_entries = sorted(
        name
        for name in names
        if PurePosixPath(name).name.casefold().startswith(
            ("avalonia.diagnostics", "cdp.integration.", "chrome.devtools.", "xaml.compiler")
        )
    )
    if debug_ui_entries:
        fail(f"运行包包含 Debug UI 自动化组件：{debug_ui_entries[0]}")
    if ".update/installed-manifest.json" in name_set:
        fail("源 ZIP 不能包含 .update/installed-manifest.json。")
    nested_archives = sorted(
        name for name in names if name.casefold().endswith(".zip") and not name.startswith("python/")
    )
    if nested_archives:
        fail(f"ZIP 包含未允许的嵌套归档：{nested_archives[0]}")

    runtime_roots = {
        name.split("/", 2)[1]
        for name in names
        if name.startswith("runtimes/") and name.count("/") >= 2
    }
    expected_runtime_roots = {args.rid, "any"}
    if runtime_roots != expected_runtime_roots:
        fail(
            "runtimes 目录不符合目标 RID："
            f"expected={sorted(expected_runtime_roots)}, actual={sorted(runtime_roots)}"
        )

    python_entries = [name for name in names if name.startswith("python/")]
    if args.flavor == "standard" and python_entries:
        fail("standard 包不能包含 python/ 目录。")
    if args.flavor == "python313":
        python_required = {
            "python/python.exe",
            "python/python313.dll",
            "python/python313.zip",
        }
        missing_python = sorted(python_required - name_set)
        if missing_python:
            fail(f"python313 包缺少运行时文件：{', '.join(missing_python)}")

    print(
        "Release ZIP 校验通过："
        f"archive={args.archive}, rid={args.rid}, flavor={args.flavor}, "
        f"files={len(names)}, uncompressed={total_size}"
    )


if __name__ == "__main__":
    configure_standard_streams()
    try:
        validate()
    except (OSError, ValueError, zipfile.BadZipFile) as exception:
        print(f"Release ZIP 校验失败：{exception}", file=sys.stderr)
        raise SystemExit(1) from exception
