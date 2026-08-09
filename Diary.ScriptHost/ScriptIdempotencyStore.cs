using System.Collections.Concurrent;
using System.Text.Json;

namespace Diary.ScriptHost;

public interface IScriptIdempotencyStore
{
    IDisposable Acquire(string scope, string key);
    bool TryGet(string scope, string key, out ScriptLogItemResult result);
    void Save(string scope, string key, ScriptLogItemResult result);
}

public sealed class ScriptIdempotencyStore : IScriptIdempotencyStore
{
    private const int DefaultMaxEntries = 2_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, object> _keyLocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PersistedEntry> _entries = new(StringComparer.Ordinal);
    private readonly string? _filePath;
    private readonly int _maxEntries;

    public ScriptIdempotencyStore(string? filePath = null, int maxEntries = DefaultMaxEntries)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        _maxEntries = maxEntries;
        Load();
    }

    public IDisposable Acquire(string scope, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var gate = _keyLocks.GetOrAdd(ComposeKey(scope, key), static _ => new object());
        Monitor.Enter(gate);
        return new MonitorLease(gate);
    }

    public bool TryGet(string scope, string key, out ScriptLogItemResult result)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(ComposeKey(scope, key), out var entry))
            {
                result = entry.Result;
                return true;
            }
        }

        result = null!;
        return false;
    }

    public void Save(string scope, string key, ScriptLogItemResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(result);
        lock (_sync)
        {
            _entries[ComposeKey(scope, key)] = new PersistedEntry(scope, key, result, DateTimeOffset.UtcNow);
            while (_entries.Count > _maxEntries)
            {
                var oldest = _entries.MinBy(item => item.Value.RecordedAt);
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
            var content = File.ReadAllText(_filePath);
            var entries = JsonSerializer.Deserialize<PersistedEntry[]>(content, JsonOptions) ?? [];
            foreach (var entry in entries.Where(item => !string.IsNullOrWhiteSpace(item.Scope) && !string.IsNullOrWhiteSpace(item.Key)))
                _entries[ComposeKey(entry.Scope, entry.Key)] = entry;
        }
        catch (JsonException)
        {
            _entries.Clear();
        }
        catch (IOException)
        {
            _entries.Clear();
        }
        catch (UnauthorizedAccessException)
        {
            _entries.Clear();
        }
    }

    private void Persist()
    {
        if (_filePath is null)
            return;
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("幂等结果文件目录无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var content = JsonSerializer.Serialize(_entries.Values.OrderByDescending(item => item.RecordedAt), JsonOptions);
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, _filePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string ComposeKey(string scope, string key) => $"{scope.Trim()}\u001f{key.Trim()}";

    private sealed record PersistedEntry(
        string Scope,
        string Key,
        ScriptLogItemResult Result,
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
