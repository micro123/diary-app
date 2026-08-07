using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    ValueTask ExportAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class ScriptExecutionHistory : IScriptExecutionHistory
{
    private readonly object _gate = new();
    private readonly LinkedList<ScriptExecutionHistoryEntry> _entries = [];
    private readonly int _capacity;
    private readonly string? _persistencePath;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ScriptExecutionHistory(int capacity = 100, string? persistencePath = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _persistencePath = persistencePath;
        Load();
    }

    public void Record(string scriptId, ScriptExecutionOutcome outcome)
    {
        var safeOutcome = outcome with { Result = ScriptDiagnosticSanitizer.Sanitize(outcome.Result) };
        lock (_gate)
        {
            _entries.AddFirst(new ScriptExecutionHistoryEntry(scriptId, safeOutcome, DateTimeOffset.UtcNow));
            while (_entries.Count > _capacity)
                _entries.RemoveLast();
            if (_persistencePath is not null)
                WriteAtomically(_persistencePath, _entries);
        }
    }

    public IReadOnlyList<ScriptExecutionHistoryEntry> GetRecent(int limit = 50)
    {
        if (limit <= 0)
            return Array.Empty<ScriptExecutionHistoryEntry>();
        lock (_gate)
            return _entries.Take(limit).ToArray();
    }

    public ValueTask ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            WriteAtomically(path, _entries);
        return ValueTask.CompletedTask;
    }

    private void Load()
    {
        if (_persistencePath is null || !File.Exists(_persistencePath))
            return;
        try
        {
            var entries = JsonSerializer.Deserialize<List<ScriptExecutionHistoryEntry>>(
                File.ReadAllText(_persistencePath), JsonOptions) ?? [];
            foreach (var entry in entries.Take(_capacity))
                _entries.AddLast(entry with
                {
                    Outcome = entry.Outcome with
                    {
                        Result = ScriptDiagnosticSanitizer.Sanitize(entry.Outcome.Result),
                    },
                });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries.Clear();
        }
    }

    private static void WriteAtomically(string path, IEnumerable<ScriptExecutionHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
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
