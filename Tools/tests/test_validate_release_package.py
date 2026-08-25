from __future__ import annotations

import importlib.util
import tempfile
import unittest
import zipfile
from pathlib import Path


SCRIPT_PATH = Path(__file__).parents[1] / "validate-release-package.py"
SPEC = importlib.util.spec_from_file_location("validate_release_package", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ValidateReleasePackageTests(unittest.TestCase):
    def test_script_api_validation_accepts_linked_examples(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            archive_path = Path(temporary) / "package.zip"
            files = {name: "" for name in MODULE.SCRIPT_API_ENTRY_DOCUMENTS}
            files["Docs/ScriptApi/CSharp.md"] = (
                "[示例](Examples/QuickStart.cs) [指南](../ScriptGuide.md)"
            )
            files["Docs/ScriptApi/Examples/QuickStart.cs"] = "// example"
            files["Docs/ScriptGuide.md"] = "guide"

            self._write_archive(archive_path, files)
            with zipfile.ZipFile(archive_path) as archive:
                MODULE.validate_script_api(archive, set(archive.namelist()))

    def test_script_api_validation_rejects_missing_packaged_path(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            archive_path = Path(temporary) / "package.zip"
            files = {name: "" for name in MODULE.SCRIPT_API_ENTRY_DOCUMENTS}
            files["Docs/ScriptApi/Python.md"] = "[说明](Examples/QuickStart.md)"
            files["Docs/ScriptApi/Examples/QuickStart.md"] = (
                "复制 `Docs/ScriptApi/Examples/QuickStart.py`。"
            )

            self._write_archive(archive_path, files)
            with zipfile.ZipFile(archive_path) as archive:
                with self.assertRaisesRegex(ValueError, "QuickStart.py"):
                    MODULE.validate_script_api(archive, set(archive.namelist()))

    def test_script_api_validation_rejects_missing_markdown_link(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            archive_path = Path(temporary) / "package.zip"
            files = {name: "" for name in MODULE.SCRIPT_API_ENTRY_DOCUMENTS}
            files["Docs/ScriptApi/Lua.md"] = "[示例](Examples/QuickStart.lua)"

            self._write_archive(archive_path, files)
            with zipfile.ZipFile(archive_path) as archive:
                with self.assertRaisesRegex(ValueError, "QuickStart.lua"):
                    MODULE.validate_script_api(archive, set(archive.namelist()))

    @staticmethod
    def _write_archive(archive_path: Path, files: dict[str, str]) -> None:
        with zipfile.ZipFile(archive_path, "w") as archive:
            for name, content in files.items():
                archive.writestr(name, content)


if __name__ == "__main__":
    unittest.main()
