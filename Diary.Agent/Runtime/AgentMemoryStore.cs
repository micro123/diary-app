using System.Security.Cryptography;
using System.Text;

namespace Diary.Agent.Runtime;

public sealed record AgentMemorySnapshot(
    string Content,
    string Version,
    int ByteLength,
    DateTimeOffset? LastWriteTimeUtc,
    bool Exists,
    bool HasBackup);

public sealed class AgentMemoryException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed class AgentMemoryStore
{
    public const int MaxBytes = 32 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _backupPath;

    public AgentMemoryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _backupPath = _path + ".bak";
    }

    public string FilePath => _path;

    public static int GetUtf8ByteCount(string content) => EncodeAndValidate(content).Length;

    public async ValueTask<AgentMemorySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadSnapshotAsync(_path, File.Exists(_backupPath), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AgentMemorySnapshot> SaveAsync(
        string content,
        string? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = EncodeAndValidate(content);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadSnapshotAsync(_path, File.Exists(_backupPath), cancellationToken);
            EnsureExpectedVersion(current, expectedVersion);
            await WriteWithBackupAsync(bytes, current, cancellationToken);
            return await ReadSnapshotAsync(_path, File.Exists(_backupPath), cancellationToken);
        }
        catch (AgentMemoryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentMemoryException("memory_write_failed", "无法保存 Agent 记忆文件。", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<AgentMemorySnapshot> RestoreBackupAsync(
        string? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadSnapshotAsync(_path, File.Exists(_backupPath), cancellationToken);
            EnsureExpectedVersion(current, expectedVersion);
            if (!File.Exists(_backupPath))
                throw new AgentMemoryException("memory_backup_missing", "没有可恢复的 Agent 记忆备份。");

            var backup = await ReadSnapshotAsync(_backupPath, false, cancellationToken);
            var restoredBytes = EncodeAndValidate(backup.Content);
            var previousBytes = EncodeAndValidate(current.Content);
            var directory = EnsureDirectory();
            var memoryTemporaryPath = CreateTemporaryPath(directory, "memory");
            var backupTemporaryPath = CreateTemporaryPath(directory, "backup");
            try
            {
                await WriteTemporaryAsync(memoryTemporaryPath, restoredBytes, cancellationToken);
                await WriteTemporaryAsync(backupTemporaryPath, previousBytes, cancellationToken);
                File.Move(backupTemporaryPath, _backupPath, overwrite: true);
                File.Move(memoryTemporaryPath, _path, overwrite: true);
            }
            finally
            {
                DeleteTemporary(memoryTemporaryPath);
                DeleteTemporary(backupTemporaryPath);
            }
            return await ReadSnapshotAsync(_path, File.Exists(_backupPath), cancellationToken);
        }
        catch (AgentMemoryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentMemoryException("memory_write_failed", "无法恢复 Agent 记忆备份。", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask WriteWithBackupAsync(
        byte[] bytes,
        AgentMemorySnapshot current,
        CancellationToken cancellationToken)
    {
        var directory = EnsureDirectory();
        var memoryTemporaryPath = CreateTemporaryPath(directory, "memory");
        var backupTemporaryPath = CreateTemporaryPath(directory, "backup");
        try
        {
            await WriteTemporaryAsync(memoryTemporaryPath, bytes, cancellationToken);
            var latest = await ReadSnapshotAsync(_path, File.Exists(_backupPath), cancellationToken);
            if (!string.Equals(latest.Version, current.Version, StringComparison.Ordinal))
                throw new AgentMemoryException("memory_conflict", "Agent 记忆已被其他操作修改，请重新加载后再保存。");

            if (latest.Exists)
            {
                await WriteTemporaryAsync(
                    backupTemporaryPath,
                    EncodeAndValidate(latest.Content),
                    cancellationToken);
                File.Move(backupTemporaryPath, _backupPath, overwrite: true);
            }
            File.Move(memoryTemporaryPath, _path, overwrite: true);
        }
        finally
        {
            DeleteTemporary(memoryTemporaryPath);
            DeleteTemporary(backupTemporaryPath);
        }
    }

    private static void EnsureExpectedVersion(AgentMemorySnapshot current, string? expectedVersion)
    {
        if (expectedVersion is not null
            && !string.Equals(current.Version, expectedVersion, StringComparison.Ordinal))
        {
            throw new AgentMemoryException("memory_conflict", "Agent 记忆已被其他操作修改，请重新加载后再保存。");
        }
    }

    private async ValueTask<AgentMemorySnapshot> ReadSnapshotAsync(
        string path,
        bool hasBackup,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return CreateSnapshot([], null, false, hasBackup);
        try
        {
            var information = new FileInfo(path);
            if (information.Length > MaxBytes)
                throw new AgentMemoryException("memory_too_large", $"Agent 记忆文件不能超过 {MaxBytes / 1024} KiB。");
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            if (bytes.Length > MaxBytes)
                throw new AgentMemoryException("memory_too_large", $"Agent 记忆文件不能超过 {MaxBytes / 1024} KiB。");
            return CreateSnapshot(bytes, information.LastWriteTimeUtc, true, hasBackup);
        }
        catch (AgentMemoryException)
        {
            throw;
        }
        catch (DecoderFallbackException exception)
        {
            throw new AgentMemoryException("memory_invalid_utf8", "Agent 记忆文件不是有效的 UTF-8 文本。", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentMemoryException("memory_read_failed", "无法读取 Agent 记忆文件。", exception);
        }
    }

    private static AgentMemorySnapshot CreateSnapshot(
        byte[] bytes,
        DateTimeOffset? lastWriteTimeUtc,
        bool exists,
        bool hasBackup)
    {
        var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        var content = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
        var version = (exists ? "file:" : "missing:")
                      + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new AgentMemorySnapshot(content, version, bytes.Length, lastWriteTimeUtc, exists, hasBackup);
    }

    private static byte[] EncodeAndValidate(string content)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(content);
        }
        catch (EncoderFallbackException exception)
        {
            throw new AgentMemoryException("memory_invalid_utf8", "Agent 记忆包含无法编码为 UTF-8 的字符。", exception);
        }
        if (bytes.Length > MaxBytes)
            throw new AgentMemoryException("memory_too_large", $"Agent 记忆不能超过 {MaxBytes / 1024} KiB。");
        return bytes;
    }

    private string EnsureDirectory()
    {
        var directory = System.IO.Path.GetDirectoryName(_path)
            ?? throw new AgentMemoryException("memory_path_invalid", "Agent 记忆文件路径缺少父目录。");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateTemporaryPath(string directory, string purpose) =>
        System.IO.Path.Combine(directory, $".agent-{purpose}-{Guid.NewGuid():N}.tmp");

    private static async ValueTask WriteTemporaryAsync(
        string temporaryPath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteTemporary(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
