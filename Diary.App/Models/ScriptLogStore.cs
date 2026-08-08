using Diary.ScriptBase;

namespace Diary.App.Models;

public sealed record ScriptLogEntry(
    DateTimeOffset Timestamp,
    ScriptLogLevel Level,
    string Message)
{
    public string LevelLabel => Level switch
    {
        ScriptLogLevel.Debug => "调试",
        ScriptLogLevel.Info => "信息",
        ScriptLogLevel.Warning => "警告",
        ScriptLogLevel.Error => "错误",
        _ => "未知",
    };

    public string DisplayText => $"{Timestamp:HH:mm:ss.fff} [{LevelLabel}] {Message}";
}

public sealed class ScriptLogStore
{
    public const int MaxEntryCount = 2000;

    private readonly object _sync = new();
    private readonly Queue<ScriptLogEntry> _entries = new();

    public event EventHandler? Changed;

    public void Append(ScriptLogLevel level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
        {
            if (_entries.Count >= MaxEntryCount)
                _entries.Dequeue();
            _entries.Enqueue(new ScriptLogEntry(DateTimeOffset.Now, level, message));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ScriptLogEntry> GetSnapshot()
    {
        lock (_sync)
            return _entries.ToArray();
    }

    public void Clear()
    {
        lock (_sync)
            _entries.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
