from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path


SCRIPT_PATH = Path(__file__).parents[1] / "merge-publish-directory.py"
SPEC = importlib.util.spec_from_file_location("merge_publish_directory", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class MergePublishDirectoryTests(unittest.TestCase):
    def test_merge_reuses_identical_files_and_copies_unique_files(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            target = root / "target"
            source.mkdir()
            target.mkdir()
            (source / "shared.dll").write_bytes(b"same")
            (target / "shared.dll").write_bytes(b"same")
            (source / "Diary.Mcp.dll").write_bytes(b"mcp")

            copied, reused, copied_bytes = MODULE.merge(source, target, True)

            self.assertEqual(1, copied)
            self.assertEqual(1, reused)
            self.assertEqual(3, copied_bytes)
            self.assertEqual(b"mcp", (target / "Diary.Mcp.dll").read_bytes())

    def test_merge_rejects_different_content_at_same_path(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            target = root / "target"
            source.mkdir()
            target.mkdir()
            (source / "shared.dll").write_bytes(b"source")
            (target / "shared.dll").write_bytes(b"target")

            with self.assertRaisesRegex(ValueError, "内容冲突"):
                MODULE.merge(source, target, True)

    def test_merge_rejects_case_only_collision_for_windows(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source"
            target = root / "target"
            source.mkdir()
            target.mkdir()
            (source / "Diary.Mcp.dll").write_bytes(b"mcp")
            (target / "diary.mcp.dll").write_bytes(b"mcp")

            with self.assertRaisesRegex(ValueError, "大小写冲突"):
                MODULE.merge(source, target, True)


if __name__ == "__main__":
    unittest.main()
