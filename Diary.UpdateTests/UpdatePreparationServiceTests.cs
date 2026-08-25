using System.IO.Compression;
using System.Security.Cryptography;
using Diary.Update;

namespace Diary.UpdateTests;

[TestClass]
public sealed class UpdatePreparationServiceTests
{
    [TestMethod]
    public async Task PrepareAsync_FromCompletePackage_CreatesReadyTransactionPlan()
    {
        using var fixture = new Fixture();
        var targetApp = "new-app"u8.ToArray();
        var targetUpdater = "#!/bin/sh\nprintf '{\"protocolVersion\":1,\"rid\":\"linux-x64\"}'\n"u8.ToArray();
        var files = new[]
        {
            CreateFile("Diary.App", targetApp, "app", executable: true),
            CreateFile("Diary.Updater", targetUpdater, "updater", executable: true),
        };
        var manifest = new UpdateManifest
        {
            ManifestFormatVersion = 1,
            VersionId = "1.0.0-r500",
            Sequence = 500,
            DataVersion = "1.0.0",
            Channel = "preview",
            Rid = "linux-x64",
            Flavor = "standard",
            MinUpdaterVersion = UpdateProtocol.UpdaterProtocolVersion,
            MinIncrementalSequence = 0,
            ManifestContentId = $"sha256:{new string('0', 64)}",
            Files = files,
        };
        manifest = manifest with
        {
            ManifestContentId = UpdateManifestValidator.ComputeContentId(manifest),
        };
        fixture.CreatePackage(new Dictionary<string, byte[]>
        {
            ["Diary.App"] = targetApp,
            ["Diary.Updater"] = targetUpdater,
        });
        await File.WriteAllTextAsync(fixture.InstallPath("Diary.App"), "old-app");
        await File.WriteAllTextAsync(fixture.InstallPath("Diary.Updater"), "old-updater");
        var oldAppSha256 = await UpdateHash.ComputeSha256Async(fixture.InstallPath("Diary.App"));
        var packageDescriptor = new UpdatePackageDescriptor
        {
            Size = new FileInfo(fixture.PackagePath).Length,
            Sha256 = await UpdateHash.ComputeSha256Async(fixture.PackagePath),
        };

        var prepared = await CreateService(new CopyPackageSource(fixture.PackagePath))
            .PrepareAsync(new UpdatePreparationRequest
            {
                ServerUri = new Uri("http://updates.local"),
                PackageUri = new Uri("http://updates.local/package.zip"),
                Envelope = new UpdateManifestEnvelope
                {
                    Manifest = manifest,
                    FullPackage = packageDescriptor,
                },
                CurrentVersion = "1.0.0-r499",
                InstallDirectory = fixture.InstallDirectory,
                UpdatesRootDirectory = fixture.UpdatesDirectory,
                RestartArguments = ["--core-only"],
            });

        var plan = await UpdateTransactionStore.LoadPlanAsync(prepared.PlanPath);
        var validated = UpdatePlanValidator.Validate(plan, prepared.PlanPath);
        var status = await new UpdateTransactionStore(validated).ReadStatusAsync();
        Assert.AreEqual(UpdateTransactionState.ReadyToApply, status!.State);
        Assert.AreEqual("1.0.0-r500", prepared.TargetVersion);
        Assert.AreEqual(UpdateDownloadMode.FullPackage, prepared.DownloadMode);
        Assert.AreEqual(2, prepared.ReplaceCount);
        Assert.AreEqual(oldAppSha256, plan.Restart!.PreviousSha256);
        CollectionAssert.AreEqual(
            new[] { "--core-only", UpdateProtocol.StartupTransactionArgument, prepared.PlanPath },
            plan.Restart.Arguments.ToArray());
        Assert.IsTrue(File.Exists(prepared.BootstrapUpdaterPath));
    }

    [TestMethod]
    public async Task PrepareAsync_WithInstalledManifest_DownloadsOnlyChangedBlobs()
    {
        using var fixture = new Fixture();
        var oldApp = "old-app"u8.ToArray();
        var targetApp = "new-app"u8.ToArray();
        var oldUpdater = "old-updater"u8.ToArray();
        var targetUpdater = "#!/bin/sh\nprintf '{\"protocolVersion\":1,\"rid\":\"linux-x64\"}'\n"u8.ToArray();
        var shared = "unchanged"u8.ToArray();
        var previousManifest = CreateManifest(499,
        [
            CreateFile("Diary.App", oldApp, "app", executable: true),
            CreateFile("Diary.Updater", oldUpdater, "updater", executable: true),
            CreateFile("shared.txt", shared, "app", executable: false),
        ]);
        var targetManifest = CreateManifest(500,
        [
            CreateFile("Diary.App", targetApp, "app", executable: true),
            CreateFile("Diary.Updater", targetUpdater, "updater", executable: true),
            CreateFile("shared.txt", shared, "app", executable: false),
        ]);
        await File.WriteAllBytesAsync(fixture.InstallPath("Diary.App"), oldApp);
        await File.WriteAllBytesAsync(fixture.InstallPath("Diary.Updater"), oldUpdater);
        await File.WriteAllBytesAsync(fixture.InstallPath("shared.txt"), shared);
        Directory.CreateDirectory(fixture.InstallPath(".update"));
        await File.WriteAllTextAsync(
            fixture.InstallPath(".update/installed-manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(previousManifest, UpdateJson.Options));
        var source = new BlobPackageSource(new Dictionary<string, byte[]>
        {
            [targetManifest.Files[0].Sha256] = targetApp,
            [targetManifest.Files[1].Sha256] = targetUpdater,
        });

        var prepared = await CreateService(source).PrepareAsync(new UpdatePreparationRequest
        {
            ServerUri = new Uri("http://updates.local"),
            PackageUri = new Uri("http://updates.local/package.zip"),
            Envelope = new UpdateManifestEnvelope
            {
                Manifest = targetManifest,
                FullPackage = new UpdatePackageDescriptor
                {
                    Size = 1024,
                    Sha256 = new string('f', 64),
                },
            },
            CurrentVersion = "1.0.0-r499",
            InstallDirectory = fixture.InstallDirectory,
            UpdatesRootDirectory = fixture.UpdatesDirectory,
        });

        Assert.AreEqual(UpdateDownloadMode.Incremental, prepared.DownloadMode);
        Assert.AreEqual(targetApp.Length + targetUpdater.Length, prepared.DownloadSize);
        Assert.IsFalse(source.FullPackageRequested);
        CollectionAssert.AreEquivalent(
            new[] { targetManifest.Files[0].Sha256, targetManifest.Files[1].Sha256 },
            source.DownloadedSha256.ToArray());
        Assert.AreEqual(2, prepared.ReplaceCount);
    }

    [TestMethod]
    public async Task EnsureMcpAvailableAsync_WhenExecutableIsLocked_ReportsActionableError()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows 可执行文件占用检查仅适用于 Windows。");
            return;
        }

        using var fixture = new Fixture();
        var mcpPath = fixture.InstallPath("Diary.Mcp.exe");
        await File.WriteAllTextAsync(mcpPath, "mcp");
        await using var locked = new FileStream(mcpPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            await UpdatePreparationService.EnsureMcpAvailableAsync(
                fixture.InstallDirectory,
                [new UpdateFileOperation
                {
                    Kind = UpdateFileOperationKind.Replace,
                    TargetPath = "Diary.Mcp.exe",
                }],
                CancellationToken.None,
                []));

        StringAssert.Contains(exception.Message, "结束当前 MCP 会话");
    }

    private static UpdatePreparationService CreateService(IUpdatePackageSource source) =>
        new(
            source,
            (_, _) => ValueTask.FromResult(new UpdateMachineVersion(
                UpdateProtocol.UpdaterProtocolVersion,
                "linux-x64")));

    private static UpdateManifest CreateManifest(long sequence, IReadOnlyList<UpdateManifestFile> files)
    {
        var manifest = new UpdateManifest
        {
            ManifestFormatVersion = 1,
            VersionId = $"1.0.0-r{sequence}",
            Sequence = sequence,
            DataVersion = "1.0.0",
            Channel = "preview",
            Rid = "linux-x64",
            Flavor = "standard",
            MinUpdaterVersion = UpdateProtocol.UpdaterProtocolVersion,
            MinIncrementalSequence = 0,
            ManifestContentId = $"sha256:{new string('0', 64)}",
            Files = files,
        };
        return manifest with { ManifestContentId = UpdateManifestValidator.ComputeContentId(manifest) };
    }

    private static UpdateManifestFile CreateFile(
        string path,
        byte[] content,
        string component,
        bool executable)
        => new()
        {
            Path = path,
            Size = content.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            Component = component,
            Executable = executable,
        };

    private sealed class CopyPackageSource(string sourcePath) : IUpdatePackageSource
    {
        public async ValueTask DownloadPackageAsync(
            Uri packageUri,
            string targetPath,
            UpdatePackageDescriptor descriptor,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath);
            await UpdateHash.VerifyFileAsync(targetPath, descriptor.Size, descriptor.Sha256, cancellationToken);
            progress?.Report(new(descriptor.Size, descriptor.Size));
        }

        public ValueTask DownloadContentAsync(
            Uri contentUri,
            string targetPath,
            UpdateManifestFile descriptor,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("完整包测试不应下载内容 Blob。");
    }

    private sealed class BlobPackageSource(IReadOnlyDictionary<string, byte[]> blobs) : IUpdatePackageSource
    {
        public bool FullPackageRequested { get; private set; }
        public List<string> DownloadedSha256 { get; } = [];

        public ValueTask DownloadPackageAsync(
            Uri packageUri,
            string targetPath,
            UpdatePackageDescriptor descriptor,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            FullPackageRequested = true;
            throw new AssertFailedException("增量更新不应请求完整包。");
        }

        public async ValueTask DownloadContentAsync(
            Uri contentUri,
            string targetPath,
            UpdateManifestFile descriptor,
            IProgress<UpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var payload = blobs[descriptor.Sha256];
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllBytesAsync(targetPath, payload, cancellationToken);
            DownloadedSha256.Add(descriptor.Sha256);
            progress?.Report(new(payload.Length, payload.Length));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"diary-preparation-tests-{Guid.NewGuid():N}");
            InstallDirectory = Path.Combine(_root, "install");
            UpdatesDirectory = Path.Combine(_root, "updates");
            PackagePath = Path.Combine(_root, "source.zip");
            Directory.CreateDirectory(InstallDirectory);
        }

        public string InstallDirectory { get; }
        public string UpdatesDirectory { get; }
        public string PackagePath { get; }

        public string InstallPath(string relativePath) => Path.Combine(InstallDirectory, relativePath);

        public void CreatePackage(IReadOnlyDictionary<string, byte[]> files)
        {
            using var archive = ZipFile.Open(PackagePath, ZipArchiveMode.Create);
            foreach (var pair in files)
            {
                var entry = archive.CreateEntry(pair.Key, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(pair.Value);
            }
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
