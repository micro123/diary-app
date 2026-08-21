using System.Security.Cryptography;
using System.Text;

namespace Diary.Update;

public static class UpdateHash
{
    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static async ValueTask<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(digest);
    }

    public static string ComputeSha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static async ValueTask VerifyFileAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("更新文件不存在。", path);
        if (info.Length != expectedSize)
            throw new InvalidDataException($"更新文件大小不匹配：{path}");
        var actual = await ComputeSha256Async(path, cancellationToken);
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"更新文件 SHA-256 不匹配：{path}");
    }
}
