from __future__ import annotations

import hashlib
import json
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
import zipfile
from pathlib import Path
from unittest.mock import patch

from diary_update_server.archive import validate_and_index
from diary_update_server.config import ServerConfig
from diary_update_server.coordinator import SyncCoordinator
from diary_update_server.http_server import create_server, start_polling
from diary_update_server.repository import UpdateRepository, compact_json
from diary_update_server.sync import ReleaseSynchronizer, file_sha256, validate_metadata


def create_package(
    path: Path,
    rid: str = "win-x64",
    flavor: str = "standard",
    marker: str = "default",
) -> None:
    names = {
        "Diary.App.dll": f"app-{marker}".encode(),
        "Diary.Script.Worker.dll": b"worker",
        "coreclr.dll": b"coreclr",
        "hostfxr.dll": b"hostfxr",
        "runtimes/win-x64/native/native.dll": b"native",
        "runtimes/any/lib/net10.0/shared.dll": b"shared",
        "Diary.App.exe": b"app-exe",
        "Diary.Script.Worker.exe": b"worker-exe",
        "Diary.Updater.exe": b"updater-exe",
        "Diary.Mcp.exe": b"mcp-exe",
        "Diary.Mcp.dll": b"mcp-dll",
        "Diary.Mcp.deps.json": b"{}",
        "Diary.Mcp.runtimeconfig.json": b"{}",
    }
    if rid == "linux-x64":
        names = {
            "Diary.App.dll": b"app",
            "Diary.Script.Worker.dll": b"worker",
            "libcoreclr.so": b"coreclr",
            "libhostfxr.so": b"hostfxr",
            "runtimes/linux-x64/native/native.so": b"native",
            "runtimes/any/lib/net10.0/shared.dll": b"shared",
            "Diary.App": b"app-exe",
            "Diary.Script.Worker": b"worker-exe",
            "Diary.Updater": b"updater-exe",
            "Diary.Mcp": b"mcp-exe",
            "Diary.Mcp.dll": b"mcp-dll",
            "Diary.Mcp.deps.json": b"{}",
            "Diary.Mcp.runtimeconfig.json": b"{}",
        }
    if flavor == "python313":
        names.update({
            "python/python.exe": b"python",
            "python/python313.dll": b"python-dll",
            "python/python313.zip": b"python-stdlib",
        })
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, content in names.items():
            info = zipfile.ZipInfo(name)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = (0o100755 if name in {"Diary.App", "Diary.Script.Worker", "Diary.Updater", "Diary.Mcp"} else 0o100644) << 16
            archive.writestr(info, content)


def metadata_for(
    package_by_variant: dict[tuple[str, str], Path],
    sequence: int = 500,
    tag: str = "v1.0.0-alpha1",
) -> dict[str, object]:
    assets = []
    for (rid, flavor), path in package_by_variant.items():
        assets.append({
            "rid": rid,
            "flavor": flavor,
            "kind": "package",
            "name": path.name,
            "size": path.stat().st_size,
            "sha256": file_sha256(path),
        })
    return {
        "schemaVersion": 1,
        "repository": "owner/repo",
        "tag": tag,
        "commit": "a" * 40,
        "versionId": f"1.0.0-r{sequence}",
        "sequence": sequence,
        "dataVersion": "1.0.0",
        "channel": "preview",
        "manifestFormatVersion": 1,
        "minUpdaterVersion": 1,
        "minIncrementalSequence": 0,
        "assets": assets,
        "debugAssets": [
            {"rid": "win-x64", "name": "windows-debug.zip"},
            {"rid": "linux-x64", "name": "linux-debug.zip"},
        ],
    }


class FakeGitHubClient:
    def __init__(self, metadata: dict[str, object], packages: dict[str, Path]):
        self.metadata = metadata
        self.packages = packages

    def list_releases(self) -> list[dict[str, object]]:
        tag = str(self.metadata["tag"])
        assets = [{"name": f"DiaryAppNG-{tag}-release-metadata.json", "browser_download_url": "memory:metadata"}]
        assets.extend({"name": name, "browser_download_url": f"memory:{name}"} for name in self.packages)
        assets.extend([
            {"name": "windows-debug.zip", "browser_download_url": "memory:windows-debug.zip"},
            {"name": "linux-debug.zip", "browser_download_url": "memory:linux-debug.zip"},
        ])
        return [{"tag_name": tag, "draft": False, "assets": assets}]

    def read_json(self, _url: str) -> dict[str, object]:
        return self.metadata

    def download(self, url: str, destination: Path) -> None:
        destination.write_bytes(self.packages[url.removeprefix("memory:")].read_bytes())


class ArchiveTests(unittest.TestCase):
    def test_archive_index_is_sorted_and_stable(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            package = root / "package.zip"
            create_package(package)
            files = validate_and_index(package, "win-x64", "standard", root / "blobs")
            self.assertEqual([item["path"] for item in files], sorted(item["path"] for item in files))
            files_by_path = {item["path"]: item for item in files}
            self.assertFalse(files_by_path["Diary.App.exe"]["executable"])
            self.assertFalse(files_by_path["Diary.Script.Worker.exe"]["executable"])
            self.assertFalse(files_by_path["Diary.Updater.exe"]["executable"])
            self.assertFalse(files_by_path["Diary.App.dll"]["executable"])
            content = {"rid": "win-x64", "flavor": "standard", "files": files}
            first = hashlib.sha256(compact_json(content)).hexdigest()
            second = hashlib.sha256(compact_json(content)).hexdigest()
            self.assertEqual(first, second)

    def test_rejects_other_runtime_directory(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            package = root / "package.zip"
            create_package(package)
            with zipfile.ZipFile(package, "a") as archive:
                archive.writestr("runtimes/linux-x64/native/foreign.so", b"foreign")
            with self.assertRaisesRegex(ValueError, "runtimes"):
                validate_and_index(package, "win-x64", "standard", root / "blobs")

    def test_rejects_path_traversal(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / "package.zip"
            create_package(package)
            with zipfile.ZipFile(package, "a") as archive:
                archive.writestr("../outside", b"bad")
            with self.assertRaisesRegex(ValueError, "非法路径段"):
                validate_and_index(package, "win-x64", "standard", Path(directory) / "blobs")

    def test_rejects_package_without_mcp_runtime_files(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source.zip"
            package = root / "package.zip"
            create_package(source)
            with zipfile.ZipFile(source) as input_archive, zipfile.ZipFile(package, "w") as output_archive:
                for info in input_archive.infolist():
                    if info.filename == "Diary.Mcp.runtimeconfig.json":
                        continue
                    output_archive.writestr(info, input_archive.read(info.filename))
            with self.assertRaisesRegex(ValueError, "Diary.Mcp.runtimeconfig.json"):
                validate_and_index(package, "win-x64", "standard", root / "blobs")


class SynchronizerTests(unittest.TestCase):
    def test_sync_publishes_all_supported_variants(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            packages: dict[tuple[str, str], Path] = {}
            for rid, flavor in (("win-x64", "standard"), ("win-x64", "python313"), ("linux-x64", "standard")):
                path = root / f"{rid}-{flavor}.zip"
                create_package(path, rid, flavor)
                packages[(rid, flavor)] = path
            metadata = metadata_for(packages)
            validate_metadata(metadata, "owner/repo", "v1.0.0-alpha1")
            config = ServerConfig(repository="owner/repo", storage_directory=root / "data")
            repository = UpdateRepository(config.storage_directory)
            client = FakeGitHubClient(metadata, {path.name: path for path in packages.values()})
            count = ReleaseSynchronizer(config, repository, client).synchronize()
            self.assertEqual(3, count)
            latest = repository.read_latest("preview", "win-x64", "standard")
            self.assertIsNotNone(latest)
            assert latest is not None
            self.assertEqual(500, latest["manifest"]["sequence"])
            self.assertTrue(str(latest["manifest"]["manifestContentId"]).startswith("sha256:"))

    def test_sync_keeps_only_latest_snapshot_and_referenced_blobs(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = ServerConfig(repository="owner/repo", storage_directory=root / "data")
            repository = UpdateRepository(config.storage_directory)

            first_packages: dict[tuple[str, str], Path] = {}
            for rid, flavor in (("win-x64", "standard"), ("win-x64", "python313"), ("linux-x64", "standard")):
                path = root / f"first-{rid}-{flavor}.zip"
                create_package(path, rid, flavor, marker="first")
                first_packages[(rid, flavor)] = path
            first_metadata = metadata_for(first_packages, sequence=500, tag="v1.0.0-alpha1")
            first_client = FakeGitHubClient(first_metadata, {path.name: path for path in first_packages.values()})
            self.assertEqual(3, ReleaseSynchronizer(config, repository, first_client).synchronize())
            first_latest = repository.read_latest("preview", "win-x64", "standard")
            assert first_latest is not None
            first_app = next(file for file in first_latest["manifest"]["files"] if file["path"] == "Diary.App.dll")
            first_app_blob = repository.blob_path(str(first_app["sha256"]))
            self.assertTrue(first_app_blob.is_file())

            second_packages: dict[tuple[str, str], Path] = {}
            for rid, flavor in (("win-x64", "standard"), ("win-x64", "python313"), ("linux-x64", "standard")):
                path = root / f"second-{rid}-{flavor}.zip"
                create_package(path, rid, flavor, marker="second")
                second_packages[(rid, flavor)] = path
            second_metadata = metadata_for(second_packages, sequence=501, tag="v1.0.0-alpha2")
            second_client = FakeGitHubClient(second_metadata, {path.name: path for path in second_packages.values()})
            self.assertEqual(3, ReleaseSynchronizer(config, repository, second_client).synchronize())

            self.assertFalse(repository.snapshot_directory("preview", 500, "win-x64", "standard").exists())
            self.assertTrue(repository.snapshot_directory("preview", 501, "win-x64", "standard").is_dir())
            self.assertFalse(first_app_blob.exists())
            retained_snapshots = [path for path in repository.snapshots.glob("*/*/*/*") if path.is_dir()]
            self.assertEqual(3, len(retained_snapshots))

    def test_metadata_requires_complete_matrix(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory) / "win.zip"
            create_package(package)
            metadata = metadata_for({("win-x64", "standard"): package})
            with self.assertRaisesRegex(ValueError, "发布矩阵不完整"):
                validate_metadata(metadata, "owner/repo", "v1.0.0-alpha1")


class HttpApiTests(unittest.TestCase):
    def test_latest_and_package_endpoints(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            repository = UpdateRepository(root / "data")
            package = root / "package.zip"
            create_package(package)
            staged = root / "staged"
            files = validate_and_index(package, "win-x64", "standard", staged)
            package_hash = file_sha256(package)
            envelope = {
                "manifest": {
                    "manifestFormatVersion": 1,
                    "versionId": "1.0.0-r500",
                    "sequence": 500,
                    "dataVersion": "1.0.0",
                    "channel": "preview",
                    "rid": "win-x64",
                    "flavor": "standard",
                    "minUpdaterVersion": 1,
                    "minIncrementalSequence": 0,
                    "manifestContentId": "sha256:" + "a" * 64,
                    "files": files,
                },
                "fullPackage": {"size": package.stat().st_size, "sha256": package_hash},
            }
            repository.publish(envelope, package, staged)
            config = ServerConfig(repository="owner/repo", storage_directory=repository.root, listen_port=0)
            server = create_server(config, repository)
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                base = f"http://127.0.0.1:{server.server_address[1]}"
                with urllib.request.urlopen(base + "/api/v1/updates/latest?channel=preview&rid=win-x64&flavor=standard") as response:
                    latest = json.load(response)
                self.assertEqual(500, latest["manifest"]["sequence"])
                with urllib.request.urlopen(base + "/api/v1/updates/packages/preview/500/win-x64/standard") as response:
                    self.assertEqual(
                        'attachment; filename="DiaryAppNG-1.0.0-r500-win-x64.zip"',
                        response.headers["Content-Disposition"],
                    )
                    self.assertEqual(int(response.headers["Content-Length"]), len(response.read()))
                with urllib.request.urlopen(base + "/downloads") as response:
                    page = response.read().decode("utf-8")
                self.assertIn("DiaryApp 完整包下载", page)
                self.assertIn("1.0.0-r500", page)
                self.assertIn("Windows x64", page)
                self.assertIn("下载完整包", page)
                self.assertIn("自动同步周期 6 小时", page)
                with self.assertRaises(urllib.error.HTTPError) as missing:
                    urllib.request.urlopen(base + "/api/v1/updates/latest?channel=stable&rid=win-x64&flavor=standard")
                self.assertEqual(404, missing.exception.code)
                missing.exception.close()
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=5)

    def test_internal_sync_requires_token_and_rejects_overlap(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            repository = UpdateRepository(Path(directory) / "data")
            config = ServerConfig(
                repository="owner/repo",
                storage_directory=repository.root,
                listen_port=0,
                sync_token_environment="TEST_DIARY_SYNC_TOKEN",
            )
            synchronizer = BlockingSynchronizer()
            coordinator = SyncCoordinator(config, repository, synchronizer)
            server = create_server(config, repository, coordinator)
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                base = f"http://127.0.0.1:{server.server_address[1]}"
                with patch.dict("os.environ", {"TEST_DIARY_SYNC_TOKEN": "secret"}):
                    unauthorized = urllib.request.Request(
                        base + "/api/v1/internal/sync",
                        data=b"",
                        method="POST",
                        headers={"Authorization": "Bearer wrong"},
                    )
                    with self.assertRaises(urllib.error.HTTPError) as unauthorized_error:
                        urllib.request.urlopen(unauthorized)
                    self.assertEqual(401, unauthorized_error.exception.code)
                    unauthorized_error.exception.close()

                    accepted = urllib.request.Request(
                        base + "/api/v1/internal/sync",
                        data=b"",
                        method="POST",
                        headers={"Authorization": "Bearer secret"},
                    )
                    with urllib.request.urlopen(accepted) as response:
                        self.assertEqual(202, response.status)
                    self.assertTrue(synchronizer.started.wait(1))

                    with self.assertRaises(urllib.error.HTTPError) as conflict:
                        urllib.request.urlopen(accepted)
                    self.assertEqual(409, conflict.exception.code)
                    conflict.exception.close()
                    synchronizer.release.set()
                    self.assertTrue(synchronizer.completed.wait(1))
            finally:
                synchronizer.release.set()
                server.shutdown()
                server.server_close()
                thread.join(timeout=5)

    def test_local_publish_uploads_package_and_exposes_latest(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            repository = UpdateRepository(root / "data")
            package = root / "local.zip"
            create_package(package, flavor="python313", marker="local")
            package_bytes = package.read_bytes()
            package_sha256 = hashlib.sha256(package_bytes).hexdigest()
            config = ServerConfig(
                repository="owner/repo",
                storage_directory=repository.root,
                listen_port=0,
                publish_token_environment="TEST_DIARY_PUBLISH_TOKEN",
            )
            server = create_server(config, repository)
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                base = f"http://127.0.0.1:{server.server_address[1]}"
                headers = {
                    "Authorization": "Bearer publish-secret",
                    "Content-Type": "application/zip",
                    "X-Diary-Channel": "local",
                    "X-Diary-Sequence": "202608210001",
                    "X-Diary-Version-Id": "1.0.0-r202608210001",
                    "X-Diary-Data-Version": "1.0.0",
                    "X-Diary-Rid": "win-x64",
                    "X-Diary-Flavor": "python313",
                    "X-Diary-Sha256": package_sha256,
                }
                with patch.dict("os.environ", {"TEST_DIARY_PUBLISH_TOKEN": "publish-secret"}):
                    unauthorized = urllib.request.Request(
                        base + "/api/v1/internal/publish/local",
                        data=package_bytes,
                        method="POST",
                        headers={**headers, "Authorization": "Bearer wrong"},
                    )
                    with self.assertRaises(urllib.error.HTTPError) as unauthorized_error:
                        urllib.request.urlopen(unauthorized)
                    self.assertEqual(401, unauthorized_error.exception.code)
                    unauthorized_error.exception.close()

                    publish = urllib.request.Request(
                        base + "/api/v1/internal/publish/local",
                        data=package_bytes,
                        method="POST",
                        headers=headers,
                    )
                    with urllib.request.urlopen(publish) as response:
                        self.assertEqual(201, response.status)
                        published = json.load(response)
                    self.assertEqual("published", published["status"])
                    self.assertEqual(202608210001, published["release"]["sequence"])
                    self.assertNotIn("files", published["release"])

                    with urllib.request.urlopen(
                        base + "/api/v1/updates/latest?channel=local&rid=win-x64&flavor=python313"
                    ) as response:
                        latest = json.load(response)
                    self.assertEqual(package_sha256, latest["fullPackage"]["sha256"])

                    retry = urllib.request.Request(
                        base + "/api/v1/internal/publish/local",
                        data=package_bytes,
                        method="POST",
                        headers=headers,
                    )
                    with urllib.request.urlopen(retry) as response:
                        self.assertEqual(200, response.status)
                        unchanged = json.load(response)
                    self.assertEqual("unchanged", unchanged["status"])
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=5)


class SchedulingTests(unittest.TestCase):
    def test_manual_sync_does_not_reset_scheduled_deadline(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            repository = UpdateRepository(Path(directory) / "data")
            config = ServerConfig(
                repository="owner/repo",
                storage_directory=repository.root,
                poll_interval_seconds=0.5,
            )
            synchronizer = RecordingSynchronizer()
            coordinator = SyncCoordinator(config, repository, synchronizer)
            stop = threading.Event()
            started_at = time.monotonic()
            scheduler = start_polling(config, coordinator, stop, delay_first_sync=True)
            try:
                time.sleep(0.2)
                self.assertTrue(coordinator.trigger_background("manual-api"))
                self.assertTrue(synchronizer.second_call.wait(1.2))
                scheduled_at = synchronizer.call_times[1] - started_at
                self.assertGreater(scheduled_at, 0.4)
                self.assertLess(scheduled_at, 0.65)
            finally:
                stop.set()
                scheduler.join(timeout=2)


class BlockingSynchronizer:
    def __init__(self) -> None:
        self.started = threading.Event()
        self.release = threading.Event()
        self.completed = threading.Event()

    def synchronize(self) -> int:
        self.started.set()
        self.release.wait(2)
        self.completed.set()
        return 0


class RecordingSynchronizer:
    def __init__(self) -> None:
        self.call_times: list[float] = []
        self.second_call = threading.Event()
        self._lock = threading.Lock()

    def synchronize(self) -> int:
        with self._lock:
            self.call_times.append(time.monotonic())
            if len(self.call_times) >= 2:
                self.second_call.set()
        return 0


if __name__ == "__main__":
    unittest.main()
