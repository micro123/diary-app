using System.Security.Cryptography;
using System.Text.Json;
using Diary.Agent.Protocols;

namespace Diary.Agent.Runtime;

public sealed record AgentConversationAttachment(
    string RelativePath,
    string FileName,
    string MediaType,
    int PixelWidth,
    int PixelHeight,
    long Length,
    string Sha256);

public sealed record AgentConversationMessage(
    string Role,
    string Content,
    IReadOnlyList<AgentConversationAttachment>? Attachments = null);

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
    private readonly string _attachmentsRoot = Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(path))
        ?? throw new InvalidOperationException("AI 会话文件目录无效。"),
        "attachments");

    public async ValueTask<IReadOnlyList<AgentConversationMessage>> CreatePersistedMessagesAsync(
        Guid conversationId,
        IEnumerable<AgentMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var result = new List<AgentConversationMessage>();
        foreach (var message in messages.Where(IsPersistable))
        {
            var attachments = new List<AgentConversationAttachment>();
            foreach (var image in message.Images)
            {
                attachments.Add(await PersistAttachmentAsync(
                    conversationId,
                    image,
                    cancellationToken));
            }
            result.Add(new AgentConversationMessage(
                message.Role == AgentMessageRole.User ? "user" : "assistant",
                message.Text,
                attachments));
        }
        return result;
    }

    public async ValueTask<IReadOnlyList<AgentImageContent>> LoadAttachmentsAsync(
        AgentConversationMessage message,
        CancellationToken cancellationToken = default)
    {
        if (message.Attachments is not { Count: > 0 })
            return [];
        var result = new List<AgentImageContent>(message.Attachments.Count);
        foreach (var attachment in message.Attachments)
        {
            var fullPath = ResolveAttachmentPath(attachment.RelativePath);
            var data = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            if (data.LongLength != attachment.Length
                || !string.Equals(
                    Convert.ToHexString(SHA256.HashData(data)),
                    attachment.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"AI 会话附件校验失败：{attachment.FileName}。");
            }
            result.Add(new AgentImageContent(
                attachment.MediaType,
                data,
                attachment.FileName,
                attachment.PixelWidth,
                attachment.PixelHeight));
        }
        return result;
    }

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
            if (envelope is null || envelope.SchemaVersion is < 1 or > 2)
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
            var originalIds = records.Select(item => item.Id).ToHashSet();
            records.RemoveAll(item => item.Id == record.Id);
            records.Add(record);
            var trimmed = records
                .OrderByDescending(item => item.UpdatedAtUtc)
                .Take(MaxConversations)
                .ToArray();
            await PersistWithoutLockAsync(trimmed, cancellationToken);
            foreach (var removedId in originalIds.Except(trimmed.Select(item => item.Id)))
                DeleteAttachmentDirectory(removedId);
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
            {
                await PersistWithoutLockAsync(records, cancellationToken);
                DeleteAttachmentDirectory(id);
            }
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
            if (envelope is null || envelope.SchemaVersion is < 1 or > 2)
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
                    new ConversationEnvelope(2, records),
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

    private static bool IsPersistable(AgentMessage message) =>
        message.Role == AgentMessageRole.User
        || (message.Role == AgentMessageRole.Assistant
            && message.ToolCalls.Count == 0
            && !string.IsNullOrWhiteSpace(message.Text));

    private async ValueTask<AgentConversationAttachment> PersistAttachmentAsync(
        Guid conversationId,
        AgentImageContent image,
        CancellationToken cancellationToken)
    {
        if (image.Data.Length == 0)
            throw new InvalidDataException("AI 会话附件不能为空。");
        var sha256 = Convert.ToHexString(SHA256.HashData(image.Data)).ToLowerInvariant();
        var extension = image.MediaType switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png",
        };
        var relativePath = Path.Combine(conversationId.ToString("N"), sha256 + extension)
            .Replace(Path.DirectorySeparatorChar, '/');
        var fullPath = ResolveAttachmentPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (!File.Exists(fullPath))
        {
            var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, image.Data, cancellationToken);
                File.Move(temporaryPath, fullPath, false);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        return new AgentConversationAttachment(
            relativePath,
            image.FileName,
            image.MediaType,
            image.PixelWidth,
            image.PixelHeight,
            image.Data.LongLength,
            sha256);
    }

    private string ResolveAttachmentPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("AI 会话附件路径无效。");
        var root = Path.GetFullPath(_attachmentsRoot) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(_attachmentsRoot, relativePath));
        if (!fullPath.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException("AI 会话附件路径越界。");
        return fullPath;
    }

    private void DeleteAttachmentDirectory(Guid conversationId)
    {
        var directory = ResolveAttachmentPath(conversationId.ToString("N"));
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
