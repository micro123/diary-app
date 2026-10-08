using System.Collections.Concurrent;
using System.Text.Json;

namespace Diary.ScriptHost;

public sealed record WorkItemExtraFieldCommand(string FieldId, string Value);

public sealed record WorkItemCreateCommand(
    string Date,
    string Title,
    double Hours,
    int Priority,
    IReadOnlyList<int> TagIds,
    IReadOnlyList<WorkItemExtraFieldCommand> ExtraFields,
    string? Note,
    string IdempotencyKey,
    string? PreviewVersion = null);

public sealed record WorkItemCommandPreview(
    bool Succeeded,
    WorkItemCreateCommand? Command,
    string? PreviewVersion,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record WorkItemCommandResult(
    bool Succeeded,
    int? WorkItemId,
    bool Duplicate,
    string? PreviewVersion,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public interface IWorkItemCommandApi
{
    ValueTask<WorkItemCommandPreview> PreviewCreateAsync(
        WorkItemCreateCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<WorkItemCommandResult> CreateAsync(
        WorkItemCreateCommand command,
        CancellationToken cancellationToken = default);
}

public interface IWorkItemCommandIdempotencyStore
{
    IDisposable Acquire(string key);

    bool TryGet(string key, out WorkItemCommandResult result);

    void Save(string key, WorkItemCommandResult result);
}

public sealed class WorkItemCommandIdempotencyStore : IWorkItemCommandIdempotencyStore
{
    private const int DefaultMaxEntries = 2_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, object> _keyLocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PersistedEntry> _entries = new(StringComparer.Ordinal);
    private readonly string? _filePath;
    private readonly int _maxEntries;

    public WorkItemCommandIdempotencyStore(string? filePath = null, int maxEntries = DefaultMaxEntries)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        _maxEntries = maxEntries;
        Load();
    }

    public IDisposable Acquire(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var gate = _keyLocks.GetOrAdd(key.Trim(), static _ => new object());
        Monitor.Enter(gate);
        return new MonitorLease(gate);
    }

    public bool TryGet(string key, out WorkItemCommandResult result)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key.Trim(), out var entry))
            {
                result = entry.Result;
                return true;
            }
        }
        result = null!;
        return false;
    }

    public void Save(string key, WorkItemCommandResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(result);
        lock (_sync)
        {
            _entries[key.Trim()] = new PersistedEntry(key.Trim(), result, DateTimeOffset.UtcNow);
            while (_entries.Count > _maxEntries)
            {
                var oldest = _entries.MinBy(pair => pair.Value.RecordedAt);
                if (oldest.Key is null)
                    break;
                _entries.Remove(oldest.Key);
            }
            Persist();
        }
    }

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath))
            return;
        try
        {
            var entries = JsonSerializer.Deserialize<PersistedEntry[]>(File.ReadAllText(_filePath), JsonOptions) ?? [];
            foreach (var entry in entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Key)))
                _entries[entry.Key] = entry;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            _entries.Clear();
        }
    }

    private void Persist()
    {
        if (_filePath is null)
            return;
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("事项命令幂等文件目录无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(_entries.Values.OrderByDescending(entry => entry.RecordedAt), JsonOptions));
            File.Move(temporaryPath, _filePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record PersistedEntry(
        string Key,
        WorkItemCommandResult Result,
        DateTimeOffset RecordedAt);

    private sealed class MonitorLease(object gate) : IDisposable
    {
        private object? _gate = gate;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is not null)
                Monitor.Exit(gate);
        }
    }
}
