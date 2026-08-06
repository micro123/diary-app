using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public sealed record ScriptExecutionHistoryEntry(
    string ScriptId,
    ScriptExecutionOutcome Outcome,
    DateTimeOffset RecordedAt);

public interface IScriptExecutionHistory
{
    void Record(string scriptId, ScriptExecutionOutcome outcome);
    IReadOnlyList<ScriptExecutionHistoryEntry> GetRecent(int limit = 50);
}

public sealed class ScriptExecutionHistory : IScriptExecutionHistory
{
    private readonly object _gate = new();
    private readonly LinkedList<ScriptExecutionHistoryEntry> _entries = [];
    private readonly int _capacity;

    public ScriptExecutionHistory(int capacity = 100)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Record(string scriptId, ScriptExecutionOutcome outcome)
    {
        var safeOutcome = outcome with { Result = ScriptDiagnosticSanitizer.Sanitize(outcome.Result) };
        lock (_gate)
        {
            _entries.AddFirst(new ScriptExecutionHistoryEntry(scriptId, safeOutcome, DateTimeOffset.UtcNow));
            while (_entries.Count > _capacity)
                _entries.RemoveLast();
        }
    }

    public IReadOnlyList<ScriptExecutionHistoryEntry> GetRecent(int limit = 50)
    {
        if (limit <= 0)
            return Array.Empty<ScriptExecutionHistoryEntry>();
        lock (_gate)
            return _entries.Take(limit).ToArray();
    }
}

public static partial class ScriptDiagnosticSanitizer
{
    [GeneratedRegex("(?i)(api[-_ ]?key|token|password|secret|authorization)\\s*[:=]\\s*[^\\s,;]+")]
    private static partial Regex SensitiveValueRegex();

    public static ScriptExecutionResult Sanitize(ScriptExecutionResult result)
    {
        var diagnostics = result.Diagnostics.IsDefault
            ? ImmutableArray<ScriptDiagnostic>.Empty
            : result.Diagnostics;
        return result with
        {
            Diagnostics = diagnostics.Select(Sanitize).ToImmutableArray(),
        };
    }

    public static ScriptDiagnostic Sanitize(ScriptDiagnostic diagnostic) =>
        diagnostic with
        {
            Message = SensitiveValueRegex().Replace(diagnostic.Message, "$1=<redacted>"),
        };
}
