using System.IO.Compression;
using System.Security.Cryptography;
using Diary.Update;

namespace Diary.UpdateTests;

[TestClass]
public sealed class UpdatePackageExtractorTests
{
    [TestMethod]
    public async Task ExtractAndValidateAsync_ExtractsManifestFiles()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["Diary.App"] = "app"u8.ToArray(),
            ["sub/config.json"] = "{}"u8.ToArray(),
        };
        using var fixture = CreateFixture(files);

        await UpdatePackageExtractor.ExtractAndValidateAsync(
            fixture.PackagePath,
            fixture.StagingPath,
            CreateManifest(files));

        CollectionAssert.AreEqual(files["Diary.App"], await File.ReadAllBytesAsync(
            Path.Combine(fixture.StagingPath, "Diary.App")));
        CollectionAssert.AreEqual(files["sub/config.json"], await File.ReadAllBytesAsync(
            Path.Combine(fixture.StagingPath, "sub", "config.json")));
        Assert.IsFalse(
            Directory.EnumerateFiles(fixture.StagingPath, "*.tmp", SearchOption.AllDirectories).Any(),
            "解压成功后不应残留仍被写入流占用的临时文件。");
    }

    [TestMethod]
    public async Task ExtractAndValidateAsync_RejectsPathTraversal()
    {
        using var fixture = CreateFixture(new Dictionary<string, byte[]>
        {
            ["../outside.txt"] = "outside"u8.ToArray(),
        });
        var manifest = CreateManifest(new Dictionary<string, byte[]>
        {
            ["safe.txt"] = "outside"u8.ToArray(),
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await UpdatePackageExtractor.ExtractAndValidateAsync(
                fixture.PackagePath,
                fixture.StagingPath,
                manifest));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.RootPath, "outside.txt")));
    }

    [TestMethod]
    public async Task ExtractAndValidateAsync_RejectsFileOutsideManifest()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["expected.txt"] = "expected"u8.ToArray(),
            ["extra.txt"] = "extra"u8.ToArray(),
        };
        using var fixture = CreateFixture(files);
        var manifest = CreateManifest(new Dictionary<string, byte[]>
        {
            ["expected.txt"] = files["expected.txt"],
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await UpdatePackageExtractor.ExtractAndValidateAsync(
                fixture.PackagePath,
                fixture.StagingPath,
                manifest));
    }

    [TestMethod]
    public async Task ExtractAndValidateAsync_RejectsHashMismatch()
    {
        var archiveFiles = new Dictionary<string, byte[]>
        {
            ["file.txt"] = "actual"u8.ToArray(),
        };
        using var fixture = CreateFixture(archiveFiles);
        var manifest = CreateManifest(new Dictionary<string, byte[]>
        {
            ["file.txt"] = "expect"u8.ToArray(),
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await UpdatePackageExtractor.ExtractAndValidateAsync(
                fixture.PackagePath,
                fixture.StagingPath,
                manifest));
    }

    [TestMethod]
    public async Task ExtractAndValidateAsync_RejectsWindowsCaseCollision()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["A.txt"] = "upper"u8.ToArray(),
            ["a.txt"] = "lower"u8.ToArray(),
        };
        using var fixture = CreateFixture(files);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await UpdatePackageExtractor.ExtractAndValidateAsync(
                fixture.PackagePath,
                fixture.StagingPath,
                CreateManifest(files, "win-x64")));
    }

    private static Fixture CreateFixture(IReadOnlyDictionary<string, byte[]> files)
    {
        var fixture = new Fixture();
        using var archive = ZipFile.Open(fixture.PackagePath, ZipArchiveMode.Create);
        foreach (var pair in files)
        {
            var entry = archive.CreateEntry(pair.Key, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(pair.Value);
        }
        return fixture;
    }

    private static UpdateManifest CreateManifest(
        IReadOnlyDictionary<string, byte[]> files,
        string rid = "linux-x64")
        => new()
        {
            ManifestFormatVersion = 1,
            VersionId = "1.0.0-r1",
            Sequence = 1,
            DataVersion = "1.0.0",
            Channel = "preview",
            Rid = rid,
            Flavor = "standard",
            MinUpdaterVersion = UpdateProtocol.UpdaterProtocolVersion,
            MinIncrementalSequence = 0,
            ManifestContentId = $"sha256:{new string('0', 64)}",
            Files = files
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new UpdateManifestFile
                {
                    Path = pair.Key,
                    Size = pair.Value.Length,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
                    Component = "app",
                })
                .ToArray(),
        };

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"diary-package-tests-{Guid.NewGuid():N}");
            PackagePath = Path.Combine(RootPath, "package.zip");
            StagingPath = Path.Combine(RootPath, "staging");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }
        public string PackagePath { get; }
        public string StagingPath { get; }

        public void Dispose() => Directory.Delete(RootPath, recursive: true);
    }
}
