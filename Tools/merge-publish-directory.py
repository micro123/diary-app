#!/usr/bin/env python3

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import sys
from pathlib import Path, PurePosixPath


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="将组件发布目录安全合并到主应用发布目录。",
    )
    parser.add_argument("--source", required=True)
    parser.add_argument("--target", required=True)
    parser.add_argument("--case-insensitive", action="store_true")
    return parser.parse_args()


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def normalized_relative(path: Path, root: Path) -> str:
    relative = path.relative_to(root).as_posix()
    normalized = PurePosixPath(relative)
    if normalized.is_absolute() or ".." in normalized.parts:
        raise ValueError(f"组件发布目录包含非法路径：{relative}")
    return str(normalized)


def collect_files(root: Path) -> dict[str, Path]:
    files: dict[str, Path] = {}
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"组件发布目录包含符号链接：{path}")
        if not path.is_file():
            continue
        relative = normalized_relative(path, root)
        if relative.casefold().endswith(".pdb"):
            raise ValueError(f"组件发布目录包含 PDB：{relative}")
        files[relative] = path
    if not files:
        raise ValueError(f"组件发布目录为空：{root}")
    return files


def target_index(root: Path, case_insensitive: bool) -> dict[str, tuple[str, Path]]:
    result: dict[str, tuple[str, Path]] = {}
    for path in sorted(root.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"主发布目录包含符号链接：{path}")
        if not path.is_file():
            continue
        relative = normalized_relative(path, root)
        key = relative.casefold() if case_insensitive else relative
        existing = result.get(key)
        if existing is not None and existing[0] != relative:
            raise ValueError(f"主发布目录包含大小写冲突：{existing[0]} / {relative}")
        result[key] = (relative, path)
    return result


def merge(source: Path, target: Path, case_insensitive: bool) -> tuple[int, int, int]:
    source = source.resolve(strict=True)
    target = target.resolve(strict=True)
    if source == target or source in target.parents or target in source.parents:
        raise ValueError("组件发布目录和主发布目录不能相同或相互包含。")

    source_files = collect_files(source)
    target_files = target_index(target, case_insensitive)
    copied = 0
    reused = 0
    copied_bytes = 0
    for relative, source_path in source_files.items():
        key = relative.casefold() if case_insensitive else relative
        existing = target_files.get(key)
        if existing is not None:
            existing_relative, target_path = existing
            if existing_relative != relative and case_insensitive:
                raise ValueError(f"组件文件与主发布目录存在大小写冲突：{relative} / {existing_relative}")
            if source_path.stat().st_size != target_path.stat().st_size or file_sha256(source_path) != file_sha256(target_path):
                raise ValueError(f"组件文件与主发布目录内容冲突：{relative}")
            reused += 1
            continue

        target_path = target.joinpath(*PurePosixPath(relative).parts)
        target_path.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source_path, target_path)
        if os.name != "nt":
            target_path.chmod(source_path.stat().st_mode)
        target_files[key] = (relative, target_path)
        copied += 1
        copied_bytes += source_path.stat().st_size
    return copied, reused, copied_bytes


def main() -> int:
    args = parse_args()
    copied, reused, copied_bytes = merge(
        Path(args.source),
        Path(args.target),
        args.case_insensitive,
    )
    print(
        "组件发布目录合并完成："
        f"copied={copied}, reused={reused}, copiedBytes={copied_bytes}"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError) as exception:
        print(f"组件发布目录合并失败：{exception}", file=sys.stderr)
        raise SystemExit(1) from exception
