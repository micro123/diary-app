using Diary.ScriptBase;

namespace Diary.App.Models;

public sealed record ScriptLogEntry(
    DateTimeOffset Timestamp,
    ScriptLogLevel Level,
    string Message,
    string? ScriptId = null)
{
    public string LevelLabel => Level switch
    {
        ScriptLogLevel.Debug => "DBG",
        ScriptLogLevel.Info => "INF",
        ScriptLogLevel.Warning => "WRN",
        ScriptLogLevel.Error => "ERR",
        _ => "UNK",
    };

    private string ScriptLabel => string.IsNullOrWhiteSpace(ScriptId)
        ? string.Empty
        : $" [{ScriptId}]";

    public string DisplayText =>
        $"[{Timestamp:MM-dd HH:mm:ss}] [{LevelLabel}]{ScriptLabel} {Message}";
}

public sealed class ScriptLogStore
{
    public const int MaxEntryCount = 2000;

    public static string FormatText(IEnumerable<ScriptLogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return string.Join(Environment.NewLine, entries.Select(entry => entry.DisplayText));
    }

    private readonly object _sync = new();
    private readonly Queue<ScriptLogEntry> _entries = new();

    public event EventHandler? Changed;

    public void Append(ScriptLogLevel level, string message, string? scriptId = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
        {
            if (_entries.Count >= MaxEntryCount)
                _entries.Dequeue();
            _entries.Enqueue(new ScriptLogEntry(DateTimeOffset.Now, level, message, scriptId));
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
