using System.IO.Compression;
using System.Security.Cryptography;

namespace Diary.Update;

public static class UpdatePackageExtractor
{
    private const int MaxFileCount = 10_000;
    private const long MaxSingleFileSize = 1024L * 1024 * 1024;
    private const long MaxTotalSize = 4L * 1024 * 1024 * 1024;
    private const long MaxCompressionRatio = 1_000;

    public static async ValueTask ExtractAndValidateAsync(
        string packagePath,
        string stagingDirectory,
        UpdateManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Directory.Exists(stagingDirectory) && Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
            throw new InvalidOperationException("更新暂存目录不是空目录。");
        Directory.CreateDirectory(stagingDirectory);

        var expected = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var windowsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        using var archive = ZipFile.OpenRead(packagePath);
        var entries = archive.Entries.Where(entry => !IsDirectory(entry)).ToArray();
        if (entries.Length == 0 || entries.Length > MaxFileCount)
            throw new InvalidDataException($"更新完整包文件数量非法：{entries.Length}");

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = UpdatePathPolicy.NormalizeRelative(entry.FullName, "ZIP entry");
            if (!seen.Add(path) || (manifest.Rid == "win-x64" && !windowsSeen.Add(path)))
                throw new InvalidDataException($"更新完整包包含重复路径：{path}");
            if (!expected.TryGetValue(path, out var expectedFile))
                throw new InvalidDataException($"更新完整包包含清单之外的文件：{path}");
            if (entry.Length != expectedFile.Size || entry.Length < 0 || entry.Length > MaxSingleFileSize)
                throw new InvalidDataException($"更新完整包文件大小与清单不匹配：{path}");
            totalSize = checked(totalSize + entry.Length);
            if (totalSize > MaxTotalSize)
                throw new InvalidDataException("更新完整包解压后总大小超过安全上限。");
            if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxCompressionRatio)
                throw new InvalidDataException($"更新完整包文件压缩比异常：{path}");
            RejectLink(entry, path);
            await ExtractEntryAsync(entry, stagingDirectory, expectedFile, cancellationToken);
        }

        var missing = expected.Keys.Where(path => !seen.Contains(path)).Take(10).ToArray();
        if (missing.Length > 0 || seen.Count != expected.Count)
            throw new InvalidDataException($"更新完整包缺少清单文件：{string.Join(", ", missing)}");
    }

    private static async ValueTask ExtractEntryAsync(
        ZipArchiveEntry entry,
        string stagingDirectory,
        UpdateManifestFile expected,
        CancellationToken cancellationToken)
    {
        var targetPath = UpdatePathPolicy.ResolveInside(stagingDirectory, expected.Path, expected.Path);
        UpdatePathPolicy.RejectExistingLinks(stagingDirectory, targetPath, expected.Path);
        var targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidDataException($"更新文件没有父目录：{expected.Path}");
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = Path.Combine(targetDirectory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using var source = entry.Open();
            await using var target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long received = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                received += read;
                if (received > expected.Size)
                    throw new InvalidDataException($"解压文件大小超过清单声明：{expected.Path}");
                digest.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await target.FlushAsync(cancellationToken);
            if (received != expected.Size)
                throw new InvalidDataException($"解压文件大小与清单不匹配：{expected.Path}");
            var actualSha256 = Convert.ToHexStringLower(digest.GetHashAndReset());
            if (!string.Equals(actualSha256, expected.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"解压文件 SHA-256 与清单不匹配：{expected.Path}");
            if (expected.Executable && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            File.Move(temporaryPath, targetPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith("/", StringComparison.Ordinal) || string.IsNullOrEmpty(entry.Name);

    private static void RejectLink(ZipArchiveEntry entry, string path)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
            throw new InvalidDataException($"更新完整包包含链接或重解析点：{path}");
    }
}
