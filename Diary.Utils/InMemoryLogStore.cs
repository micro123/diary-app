using System.Globalization;
using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace Diary.Utils;

public sealed record InMemoryLogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogEventLevel Level,
    string Source,
    string Message,
    string Properties,
    string? Exception)
{
    public string ToDisplayText()
    {
        var builder = new StringBuilder()
            .Append('[').Append(Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.CurrentCulture)).Append("] ")
            .Append('[').Append(Level.ToString().ToUpperInvariant()).Append("] ")
            .Append('[').Append(Source).Append("] ")
            .AppendLine(Message);

        if (!string.IsNullOrWhiteSpace(Properties))
            builder.AppendLine().AppendLine("结构化属性：").AppendLine(Properties);
        if (!string.IsNullOrWhiteSpace(Exception))
            builder.AppendLine().AppendLine("异常：").Append(Exception);
        return builder.ToString().TrimEnd();
    }
}

public sealed class InMemoryLogStore
{
    private readonly object _sync = new();
    private readonly Queue<InMemoryLogEntry> _entries;
    private long _nextSequence;

    public InMemoryLogStore(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "日志容量必须大于 0。");
        Capacity = capacity;
        _entries = new Queue<InMemoryLogEntry>(capacity);
    }

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    public event EventHandler? Changed;

    internal void Append(InMemoryLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            _entries.Enqueue(entry with { Sequence = ++_nextSequence });
            while (_entries.Count > Capacity)
                _entries.Dequeue();
        }
        RaiseChanged();
    }

    public IReadOnlyList<InMemoryLogEntry> GetSnapshot()
    {
        lock (_sync)
            return _entries.ToArray();
    }

    public void Clear()
    {
        var changed = false;
        lock (_sync)
        {
            if (_entries.Count > 0)
            {
                _entries.Clear();
                changed = true;
            }
        }
        if (changed)
            RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (Changed is not { } handlers)
            return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
                // 日志观察者不能反向影响业务日志写入，且此处不能再次记录日志以免递归。
            }
        }
    }
}

internal sealed class InMemoryLogSink(InMemoryLogStore store) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var source = logEvent.Properties.TryGetValue("SourceContext", out var sourceValue)
            ? RenderSource(sourceValue)
            : "Diary.App";
        var properties = string.Join(
            Environment.NewLine,
            logEvent.Properties
                .Where(pair => !string.Equals(pair.Key, "SourceContext", StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}: {pair.Value}"));

        store.Append(new InMemoryLogEntry(
            0,
            logEvent.Timestamp,
            logEvent.Level,
            source,
            logEvent.RenderMessage(CultureInfo.CurrentCulture),
            properties,
            logEvent.Exception?.ToString()));
    }

    private static string RenderSource(LogEventPropertyValue value) => value switch
    {
        ScalarValue { Value: string text } => text,
        ScalarValue { Value: not null } scalar => Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) ?? "Diary.App",
        _ => value.ToString(),
    };
}
