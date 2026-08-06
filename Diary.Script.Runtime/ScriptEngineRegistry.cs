using System.Collections.Immutable;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public sealed record ScriptRegistrationResult(
    bool Succeeded,
    ImmutableArray<ScriptDiagnostic> Diagnostics)
{
    public static ScriptRegistrationResult Success() =>
        new(true, ImmutableArray<ScriptDiagnostic>.Empty);

    public static ScriptRegistrationResult Failure(ScriptDiagnostic diagnostic) =>
        new(false, [diagnostic]);
}

public sealed record ScriptEngineSelectionResult(
    IScriptEngineV1? Engine,
    ImmutableArray<ScriptDiagnostic> Diagnostics)
{
    public bool Succeeded => Engine is not null;
}

public interface IScriptEngineRegistry
{
    ScriptRegistrationResult Register(IScriptEngineV1 engine);
    ScriptEngineSelectionResult Select(ScriptMatchRequest request);
}

public sealed class ScriptEngineRegistry : IScriptEngineRegistry
{
    private readonly object _gate = new();
    private readonly List<EngineEntry> _engines = [];

    public ScriptRegistrationResult Register(IScriptEngineV1 engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        string stableName;
        try
        {
            stableName = engine.StableName;
        }
        catch (Exception)
        {
            return ScriptRegistrationResult.Failure(Diagnostic(
                "SCRIPT_ENGINE_NAME_FAILED",
                "The script engine stable name could not be read."));
        }

        if (string.IsNullOrWhiteSpace(stableName))
        {
            return ScriptRegistrationResult.Failure(Diagnostic(
                "SCRIPT_ENGINE_NAME_INVALID",
                "The script engine stable name must not be empty."));
        }

        lock (_gate)
        {
            if (_engines.Any(entry => StringComparer.Ordinal.Equals(entry.StableName, stableName)))
            {
                return ScriptRegistrationResult.Failure(Diagnostic(
                    "SCRIPT_ENGINE_DUPLICATE",
                    $"A script engine named '{stableName}' is already registered."));
            }

            _engines.Add(new EngineEntry(stableName, engine));
        }

        return ScriptRegistrationResult.Success();
    }

    public ScriptEngineSelectionResult Select(ScriptMatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EngineEntry[] engines;
        lock (_gate)
            engines = [.. _engines];

        IScriptEngineV1? selected = null;
        var selectedPriority = int.MinValue;
        var diagnostics = ImmutableArray.CreateBuilder<ScriptDiagnostic>();
        foreach (var entry in engines)
        {
            try
            {
                var match = entry.Engine.Match(request);
                if (match is { IsMatch: true } && (selected is null || match.Priority > selectedPriority))
                {
                    selected = entry.Engine;
                    selectedPriority = match.Priority;
                }
            }
            catch (Exception)
            {
                diagnostics.Add(new ScriptDiagnostic(
                    "SCRIPT_ENGINE_MATCH_EXCEPTION",
                    $"Script engine '{entry.StableName}' failed while matching the source.",
                    ScriptDiagnosticSeverity.Warning,
                    ScriptDiagnosticCategory.Engine,
                    request.SourcePath));
            }
        }

        if (selected is null)
        {
            diagnostics.Add(new ScriptDiagnostic(
                "SCRIPT_ENGINE_NOT_FOUND",
                "No registered script engine matched the source.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Engine,
                request.SourcePath));
        }

        return new ScriptEngineSelectionResult(selected, diagnostics.ToImmutable());
    }

    private static ScriptDiagnostic Diagnostic(string code, string message) =>
        new(code, message, ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Engine);

    private sealed record EngineEntry(string StableName, IScriptEngineV1 Engine);
}
