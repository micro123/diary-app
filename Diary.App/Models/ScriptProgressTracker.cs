using Diary.ScriptBase;

namespace Diary.App.Models;

public sealed record ScriptProgressSnapshot(
    double Fraction,
    string Message,
    DateTimeOffset UpdatedAt)
{
    public string DisplayText => $"{Fraction:P0} {Message}";
}

public sealed class ScriptProgressTranscript(string executionId)
{
    public const int MaxEntries = 50;

    private readonly List<ScriptProgressSnapshot> _entries = [];
    public string ExecutionId { get; } = executionId;
    public ScriptProgressSnapshot? Latest { get; private set; }

    public void Append(ScriptProgressSnapshot snapshot)
    {
        Latest = snapshot;
        if (_entries.Count >= MaxEntries)
            _entries.RemoveAt(0);
        _entries.Add(snapshot);
    }

    public IReadOnlyList<ScriptProgressSnapshot> Entries => _entries;
}

public sealed class ScriptProgressTracker
{
    public const int MaxExecutions = 20;

    private readonly object _sync = new();
    private readonly Dictionary<string, ScriptProgressTranscript> _transcripts = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public event EventHandler? Changed;

    /// <summary>最近一次上报的进度快照（管理页运行区展示用；脚本执行在页面内串行）。</summary>
    public ScriptProgressSnapshot? LastReported { get; private set; }

    public void Report(string executionId, ScriptProgressUpdate update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        if (double.IsNaN(update.Fraction) || update.Fraction < 0 || update.Fraction > 1)
            throw new ArgumentOutOfRangeException(nameof(update), "进度必须位于 0 到 1 之间。");
        if (string.IsNullOrWhiteSpace(update.Message))
            throw new ArgumentException("进度消息不能为空。", nameof(update));
        lock (_sync)
        {
            if (!_transcripts.TryGetValue(executionId, out var transcript))
            {
                if (_order.Count >= MaxExecutions)
                {
                    var oldest = _order.Dequeue();
                    _transcripts.Remove(oldest);
                }
                transcript = new ScriptProgressTranscript(executionId);
                _transcripts[executionId] = transcript;
                _order.Enqueue(executionId);
            }
            var snapshot = new ScriptProgressSnapshot(update.Fraction, update.Message, DateTimeOffset.Now);
            transcript.Append(snapshot);
            LastReported = snapshot;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ScriptProgressSnapshot? Get(string executionId)
    {
        lock (_sync)
            return _transcripts.TryGetValue(executionId, out var transcript) ? transcript.Latest : null;
    }

    public IReadOnlyList<ScriptProgressSnapshot> GetTranscript(string executionId)
    {
        lock (_sync)
            return _transcripts.TryGetValue(executionId, out var transcript)
                ? transcript.Entries.ToArray()
                : [];
    }

    public void Clear()
    {
        lock (_sync)
        {
            _transcripts.Clear();
            _order.Clear();
            LastReported = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
