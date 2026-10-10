using System.Text.Json;
using Diary.Agent.Protocols;

namespace Diary.Agent.Runtime;

public sealed record AgentConversationMessage(string Role, string Content);

public sealed record AgentConversationToolCall(
    string Name,
    string ArgumentsSummary,
    string Status,
    string? Source,
    string? Duration);

public sealed record AgentConversationRecord(
    Guid Id,
    string Title,
    string ConnectionId,
    string Model,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<AgentConversationMessage> Messages,
    IReadOnlyList<AgentConversationToolCall> ToolCalls,
    string LastRunStatus,
    AgentUsage? Usage,
    string? ContextSummary = null,
    int ContextCompactionCount = 0);

public static class AgentConversationProjection
{
    public static IReadOnlyList<AgentConversationMessage> CreatePersistedMessages(
        IEnumerable<AgentMessage> messages) => messages
        .Where(message => message.Role == AgentMessageRole.User
                          || (message.Role == AgentMessageRole.Assistant
                              && message.ToolCalls.Count == 0
                              && !string.IsNullOrWhiteSpace(message.Text)))
        .Select(message => new AgentConversationMessage(
            message.Role == AgentMessageRole.User ? "user" : "assistant",
            message.Text))
        .ToArray();
}

public sealed class AgentConversationStore(string path)
{
    private const int MaxConversations = 50;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<IReadOnlyList<AgentConversationRecord>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
                return [];
            await using var stream = File.OpenRead(path);
            var envelope = await JsonSerializer.DeserializeAsync<ConversationEnvelope>(
                stream,
                JsonOptions,
                cancellationToken);
            if (envelope is null || envelope.SchemaVersion != 1)
                return [];
            return envelope.Conversations
                .OrderByDescending(item => item.UpdatedAtUtc)
                .Take(MaxConversations)
                .ToArray();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        AgentConversationRecord record,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await LoadWithoutLockAsync(cancellationToken);
            records.RemoveAll(item => item.Id == record.Id);
            records.Add(record);
            var trimmed = records
                .OrderByDescending(item => item.UpdatedAtUtc)
                .Take(MaxConversations)
                .ToArray();
            await PersistWithoutLockAsync(trimmed, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = await LoadWithoutLockAsync(cancellationToken);
            if (records.RemoveAll(item => item.Id == id) > 0)
                await PersistWithoutLockAsync(records, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<AgentConversationRecord>> LoadWithoutLockAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            await using var stream = File.OpenRead(path);
            var envelope = await JsonSerializer.DeserializeAsync<ConversationEnvelope>(
                stream,
                JsonOptions,
                cancellationToken);
            if (envelope is null || envelope.SchemaVersion != 1)
                throw new JsonException("AI 会话文件 schemaVersion 无效。");
            return envelope.Conversations.ToList();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("AI 会话文件不可读取，已阻止覆盖。", exception);
        }
    }

    private async Task PersistWithoutLockAsync(
        IReadOnlyList<AgentConversationRecord> records,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("AI 会话文件目录无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new ConversationEnvelope(1, records),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record ConversationEnvelope(
        int SchemaVersion,
        IReadOnlyList<AgentConversationRecord> Conversations);
}
