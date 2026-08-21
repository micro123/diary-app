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
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("目标更新器探针集成测试使用 Unix 可执行脚本。");
            return;
        }

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

        var prepared = await new UpdatePreparationService(new CopyPackageSource(fixture.PackagePath))
            .PrepareAsync(new UpdatePreparationRequest
            {
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
        Assert.AreEqual(2, prepared.ReplaceCount);
        Assert.AreEqual(oldAppSha256, plan.Restart!.PreviousSha256);
        CollectionAssert.AreEqual(
            new[] { "--core-only", UpdateProtocol.StartupTransactionArgument, prepared.PlanPath },
            plan.Restart.Arguments.ToArray());
        Assert.IsTrue(File.Exists(prepared.BootstrapUpdaterPath));
        Assert.AreEqual(
            UpdateProtocol.UpdaterProtocolVersion,
            (await UpdateProcessServices.ProbeUpdaterAsync(prepared.BootstrapUpdaterPath)).ProtocolVersion);
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
