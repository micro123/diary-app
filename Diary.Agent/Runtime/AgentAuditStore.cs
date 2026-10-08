using System.Text;
using System.Text.Json;
using Diary.Agent.Protocols;

namespace Diary.Agent.Runtime;

public sealed record AgentRunAuditRecord(
    Guid RunId,
    string ConnectionId,
    string Model,
    string Protocol,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string Status,
    int Rounds,
    int ToolCalls,
    string? ErrorCode,
    AgentUsage? Usage);

public sealed record AgentToolAuditRecord(
    Guid RunId,
    Guid InvocationId,
    string ToolId,
    string ModelName,
    string Origin,
    string Risk,
    DateTimeOffset StartedAtUtc,
    long DurationMilliseconds,
    bool Succeeded,
    string? ErrorCode,
    bool IsExternalContent,
    bool IsTruncated,
    int ResultCharacters,
    string? EffectSummary);

public interface IAgentAuditStore
{
    ValueTask RecordRunAsync(AgentRunAuditRecord record, CancellationToken cancellationToken = default);

    ValueTask RecordToolAsync(AgentToolAuditRecord record, CancellationToken cancellationToken = default);
}

public sealed class AgentAuditStore : IAgentAuditStore
{
    private const long DefaultMaxBytes = 5 * 1024 * 1024;
    private const int DefaultMaxArchiveFiles = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxArchiveFiles;

    public AgentAuditStore(
        string path,
        long maxBytes = DefaultMaxBytes,
        int maxArchiveFiles = DefaultMaxArchiveFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxArchiveFiles);
        _path = path;
        _maxBytes = maxBytes;
        _maxArchiveFiles = maxArchiveFiles;
    }

    public ValueTask RecordRunAsync(AgentRunAuditRecord record, CancellationToken cancellationToken = default) =>
        AppendAsync(new AuditEnvelope("run", record), cancellationToken);

    public ValueTask RecordToolAsync(AgentToolAuditRecord record, CancellationToken cancellationToken = default) =>
        AppendAsync(new AuditEnvelope("tool", record), cancellationToken);

    private async ValueTask AppendAsync(AuditEnvelope envelope, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fullPath = Path.GetFullPath(_path);
            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("AI 审计文件目录无效。");
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(envelope, JsonOptions) + Environment.NewLine;
            RotateIfNeeded(fullPath, Encoding.UTF8.GetByteCount(line));
            await File.AppendAllTextAsync(fullPath, line, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RotateIfNeeded(string fullPath, int incomingBytes)
    {
        if (!File.Exists(fullPath))
            return;
        var currentLength = new FileInfo(fullPath).Length;
        if (currentLength == 0 || currentLength + incomingBytes <= _maxBytes)
            return;
        if (_maxArchiveFiles == 0)
        {
            File.Delete(fullPath);
            return;
        }
        for (var index = _maxArchiveFiles; index >= 2; index--)
        {
            var source = fullPath + "." + (index - 1);
            if (File.Exists(source))
                File.Move(source, fullPath + "." + index, overwrite: true);
        }
        File.Move(fullPath, fullPath + ".1", overwrite: true);
    }

    private sealed record AuditEnvelope(string Type, object Record);
}

public sealed class NullAgentAuditStore : IAgentAuditStore
{
    public ValueTask RecordRunAsync(AgentRunAuditRecord record, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask RecordToolAsync(AgentToolAuditRecord record, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
