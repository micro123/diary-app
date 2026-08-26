using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.Messaging;
using Diary.GUIBase.Events;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Services;

public sealed record AppNotificationEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastOccurredAt,
    NotificationType Type,
    NotificationRetention Retention,
    string Title,
    string Message,
    int OccurrenceCount,
    bool IsRead,
    NotificationAction? Action);

internal sealed record NotificationHistoryDocument(
    int Version,
    IReadOnlyList<AppNotificationEntry> Entries);

[DiAutoRegister(singleton: true)]
public sealed class NotificationHistoryService : IDisposable
{
    public const int MaxEntries = 100;
    public const int MaxTitleLength = 256;
    public const int MaxMessageLength = 4096;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMinutes(10);

    private const int DocumentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly Regex SensitiveValuePattern = new(
        @"(?ix)
        (?<key>api[-_ ]?key|token|password|secret)\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;]+)
        |
        (?<authorization>authorization)\s*[:=]\s*(?:(?:bearer|basic)\s+)?[^\s,;]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly object _sync = new();
    private readonly object _persistSync = new();
    private readonly string _statePath;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _subscribedToEvents;
    private readonly List<AppNotificationEntry> _entries = [];
    private Task _persistTask = Task.CompletedTask;

    public event EventHandler? Changed;

    public NotificationHistoryService(ILogger<NotificationHistoryService> logger)
        : this(
            Path.Combine(
                FsTools.GetApplicationDataDirectory(),
                "NotificationState",
                "history.json"),
            logger,
            TimeProvider.System,
            subscribeToEvents: true)
    {
    }

    internal NotificationHistoryService(
        string statePath,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : this(statePath, logger, timeProvider ?? TimeProvider.System, subscribeToEvents: false)
    {
    }

    private NotificationHistoryService(
        string statePath,
        ILogger logger,
        TimeProvider timeProvider,
        bool subscribeToEvents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        _statePath = Path.GetFullPath(statePath);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider;
        _subscribedToEvents = subscribeToEvents;
        Load();
        if (subscribeToEvents)
        {
            WeakReferenceMessenger.Default.Register<ToastEvent>(this, (_, message) => Add(message));
            WeakReferenceMessenger.Default.Register<NotifyEvent>(this, (_, message) =>
                Add(message.Value, ResolveNotificationType(message.Value.Title)));
        }
    }

    public IReadOnlyList<AppNotificationEntry> GetSnapshot()
    {
        lock (_sync)
            return _entries.ToArray();
    }

    public int UnreadCount
    {
        get
        {
            lock (_sync)
                return _entries.Count(entry => !entry.IsRead);
        }
    }

    public AppNotificationEntry? Add(
        string title,
        string? message,
        NotificationType type,
        NotificationRetention retention,
        NotificationAction? action = null)
    {
        if (retention == NotificationRetention.Transient)
            return null;

        var now = _timeProvider.GetUtcNow();
        var normalizedTitle = NormalizeText(title, MaxTitleLength, "通知");
        var normalizedMessage = NormalizeText(message, MaxMessageLength, string.Empty);
        var normalizedAction = NormalizeAction(action);
        AppNotificationEntry result;
        var persistentChanged = retention == NotificationRetention.Persistent;
        lock (_sync)
        {
            persistentChanged |= PruneExpiredLocked(now);
            var existingIndex = _entries.FindIndex(entry =>
                entry.Retention == retention
                && entry.Type == type
                && entry.Title == normalizedTitle
                && entry.Message == normalizedMessage
                && entry.Action == normalizedAction
                && now - entry.LastOccurredAt <= DeduplicationWindow);
            if (existingIndex >= 0)
            {
                var existing = _entries[existingIndex];
                result = existing with
                {
                    LastOccurredAt = now,
                    OccurrenceCount = existing.OccurrenceCount + 1,
                    IsRead = false,
                };
                _entries.RemoveAt(existingIndex);
                _entries.Insert(0, result);
            }
            else
            {
                result = new AppNotificationEntry(
                    Guid.NewGuid(),
                    now,
                    now,
                    type,
                    retention,
                    normalizedTitle,
                    normalizedMessage,
                    1,
                    false,
                    normalizedAction);
                _entries.Insert(0, result);
            }
            persistentChanged |= TrimToCapacityLocked();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (persistentChanged)
            SchedulePersist();
        return result;
    }

    public AppNotificationEntry? Add(ToastEvent toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        var title = string.IsNullOrWhiteSpace(toast.Title) ? toast.Value : toast.Title;
        var message = string.IsNullOrWhiteSpace(toast.Title) ? string.Empty : toast.Value;
        return Add(title, message, toast.Type, toast.Retention, toast.Action);
    }

    public AppNotificationEntry? Add(NotifyOptions notification, NotificationType type)
    {
        ArgumentNullException.ThrowIfNull(notification);
        return Add(
            notification.Title,
            notification.Body,
            type,
            notification.Retention,
            notification.Action);
    }

    public static NotificationType ResolveNotificationType(string title)
        => title.Contains("失败", StringComparison.Ordinal)
            || title.Contains("错误", StringComparison.Ordinal)
            || title.Contains("异常", StringComparison.Ordinal)
            ? NotificationType.Error
            : title.Contains("警告", StringComparison.Ordinal)
                || title.Contains("不可用", StringComparison.Ordinal)
                || title.Contains("无法", StringComparison.Ordinal)
                ? NotificationType.Warning
                : title.Contains("完成", StringComparison.Ordinal)
                    || title.Contains("成功", StringComparison.Ordinal)
                    ? NotificationType.Success
                    : NotificationType.Information;

    public void MarkRead(Guid id)
        => UpdateReadState(entry => entry.Id == id, true);

    public void MarkAllRead()
        => UpdateReadState(_ => true, true);

    public void Remove(Guid id)
    {
        bool changed;
        bool persistentChanged;
        lock (_sync)
        {
            var entry = _entries.FirstOrDefault(item => item.Id == id);
            changed = entry is not null && _entries.Remove(entry);
            persistentChanged = changed && entry!.Retention == NotificationRetention.Persistent;
        }
        if (!changed)
            return;
        Changed?.Invoke(this, EventArgs.Empty);
        if (persistentChanged)
            SchedulePersist();
    }

    public void ClearRead()
        => RemoveWhere(entry => entry.IsRead);

    public void ClearAll()
        => RemoveWhere(_ => true);

    public Task FlushAsync()
    {
        lock (_persistSync)
            return _persistTask;
    }

    public void Dispose()
    {
        if (_subscribedToEvents)
            WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private void UpdateReadState(Func<AppNotificationEntry, bool> predicate, bool isRead)
    {
        var changed = false;
        var persistentChanged = false;
        lock (_sync)
        {
            for (var index = 0; index < _entries.Count; index++)
            {
                var entry = _entries[index];
                if (!predicate(entry) || entry.IsRead == isRead)
                    continue;
                _entries[index] = entry with { IsRead = isRead };
                changed = true;
                persistentChanged |= entry.Retention == NotificationRetention.Persistent;
            }
        }
        if (!changed)
            return;
        Changed?.Invoke(this, EventArgs.Empty);
        if (persistentChanged)
            SchedulePersist();
    }

    private void RemoveWhere(Func<AppNotificationEntry, bool> predicate)
    {
        bool persistentChanged;
        int removed;
        lock (_sync)
        {
            persistentChanged = _entries.Any(entry => predicate(entry)
                && entry.Retention == NotificationRetention.Persistent);
            removed = _entries.RemoveAll(entry => predicate(entry));
        }
        if (removed == 0)
            return;
        Changed?.Invoke(this, EventArgs.Empty);
        if (persistentChanged)
            SchedulePersist();
    }

    private void Load()
    {
        if (!File.Exists(_statePath))
            return;
        try
        {
            var document = JsonSerializer.Deserialize<NotificationHistoryDocument>(
                File.ReadAllBytes(_statePath),
                JsonOptions);
            if (document is null || document.Version != DocumentVersion)
                return;
            var now = _timeProvider.GetUtcNow();
            bool persistentChanged;
            lock (_sync)
            {
                _entries.AddRange(document.Entries
                    .Where(entry => entry.Retention == NotificationRetention.Persistent)
                    .OrderByDescending(entry => entry.LastOccurredAt));
                persistentChanged = PruneExpiredLocked(now);
                persistentChanged |= TrimToCapacityLocked();
            }
            if (persistentChanged)
                SchedulePersist();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            _logger.LogWarning(exception, "读取通知历史失败：{StatePath}", _statePath);
        }
    }

    private void SchedulePersist()
    {
        NotificationHistoryDocument snapshot;
        lock (_sync)
        {
            snapshot = new NotificationHistoryDocument(
                DocumentVersion,
                _entries.Where(entry => entry.Retention == NotificationRetention.Persistent).ToArray());
        }
        lock (_persistSync)
        {
            _persistTask = _persistTask.ContinueWith(
                _ => PersistAsync(snapshot),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }

    private async Task PersistAsync(NotificationHistoryDocument document)
    {
        var directory = Path.GetDirectoryName(_statePath)!;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            await File.WriteAllBytesAsync(temporaryPath, bytes).ConfigureAwait(false);
            File.Move(temporaryPath, _statePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "保存通知历史失败：{StatePath}", _statePath);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(exception, "清理通知历史临时文件失败：{TemporaryPath}", temporaryPath);
            }
        }
    }

    private bool PruneExpiredLocked(DateTimeOffset now)
    {
        var persistentChanged = _entries.Any(entry =>
            entry.Retention == NotificationRetention.Persistent
            && now - entry.LastOccurredAt > MaxAge);
        _entries.RemoveAll(entry => now - entry.LastOccurredAt > MaxAge);
        return persistentChanged;
    }

    private bool TrimToCapacityLocked()
    {
        var persistentChanged = false;
        while (_entries.Count > MaxEntries)
        {
            var oldestRead = _entries.FindLastIndex(entry => entry.IsRead);
            var removeIndex = oldestRead >= 0 ? oldestRead : _entries.Count - 1;
            persistentChanged |= _entries[removeIndex].Retention == NotificationRetention.Persistent;
            _entries.RemoveAt(removeIndex);
        }
        return persistentChanged;
    }

    private static NotificationAction? NormalizeAction(NotificationAction? action)
    {
        if (action is null
            || string.IsNullOrWhiteSpace(action.Label)
            || string.IsNullOrWhiteSpace(action.Command))
            return null;
        return action with
        {
            Label = NormalizeText(action.Label, 64, string.Empty),
            Command = NormalizeText(action.Command, 128, string.Empty),
            Argument = action.Argument is null
                ? null
                : NormalizeText(action.Argument, 2048, string.Empty),
        };
    }

    private static string NormalizeText(string? value, int maxLength, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        text = SensitiveValuePattern.Replace(text, match =>
        {
            var key = match.Groups["key"].Success
                ? match.Groups["key"].Value
                : match.Groups["authorization"].Value;
            return $"{key}=[REDACTED]";
        });
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}
