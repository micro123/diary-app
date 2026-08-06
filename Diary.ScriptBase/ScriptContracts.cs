using System.Collections.Immutable;

namespace Diary.ScriptBase;

public enum ScriptApiVersion
{
    V1 = 1,
}

public static class ScriptApiVersions
{
    public const ScriptApiVersion Current = ScriptApiVersion.V1;
}

[Flags]
public enum ScriptCapability
{
    None = 0,
    ReadDiary = 1,
    WriteDiary = 2,
    UserInteraction = 4,
    Clipboard = 8,
    Tracker = 16,
}

public enum ScriptScope
{
    Application = 1,
    Editor = 2,
}

public sealed record ScriptDescriptor(
    string Id,
    string Name,
    ScriptApiVersion ApiVersion,
    ScriptScope Scope,
    ScriptCapability Capabilities,
    string? Description = null);

public enum ScriptDiagnosticSeverity
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

public enum ScriptDiagnosticCategory
{
    Syntax = 1,
    Validation = 2,
    Security = 3,
    Engine = 4,
    Runtime = 5,
    Host = 6,
}

public sealed record ScriptDiagnostic(
    string Code,
    string Message,
    ScriptDiagnosticSeverity Severity,
    ScriptDiagnosticCategory Category,
    string? SourcePath = null,
    int? Line = null,
    int? Column = null);

public sealed record ScriptBuildRequest(
    string SourcePath,
    string Source,
    ScriptApiVersion ApiVersion = ScriptApiVersion.V1);

public sealed record ScriptBuildResult(
    bool Succeeded,
    IScriptProgramV1? Program,
    ImmutableArray<ScriptDiagnostic> Diagnostics)
{
    public static ScriptBuildResult Success(IScriptProgramV1 program) =>
        new(true, program, ImmutableArray<ScriptDiagnostic>.Empty);

    public static ScriptBuildResult Failure(params ScriptDiagnostic[] diagnostics) =>
        new(false, null, [.. diagnostics]);
}

public enum ScriptExecutionStatus
{
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
    Rejected = 4,
    TimedOut = 5,
}

public sealed record EditorScriptContext(string StartDate, string EndDate);

public sealed record ScriptTarget(ScriptScope Scope, EditorScriptContext? Editor = null);

public sealed record ScriptExecutionRequest(
    ScriptTarget Target,
    ImmutableDictionary<string, string>? Arguments = null);

public sealed record ScriptExecutionResult(
    ScriptExecutionStatus Status,
    ImmutableArray<ScriptDiagnostic> Diagnostics)
{
    public static ScriptExecutionResult Succeeded() =>
        new(ScriptExecutionStatus.Succeeded, ImmutableArray<ScriptDiagnostic>.Empty);

    public static ScriptExecutionResult Cancelled() =>
        new(ScriptExecutionStatus.Cancelled, ImmutableArray<ScriptDiagnostic>.Empty);
}

public sealed record ScriptMatchRequest(string SourcePath);

public sealed record ScriptMatchResult(bool IsMatch, int Priority = 0);

public interface IScriptExecutionContext
{
    ScriptCapability Capabilities { get; }

    TApi? GetApi<TApi>() where TApi : class;
}

public interface IScriptProgramV1
{
    ScriptDescriptor Descriptor { get; }

    ValueTask<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        CancellationToken cancellationToken = default);
}

public interface IScriptEngineV1
{
    string Name { get; }
    string StableName => Name;
    string Version { get; }

    ScriptMatchResult Match(ScriptMatchRequest request);

    ValueTask<ScriptBuildResult> BuildAsync(
        ScriptBuildRequest request,
        CancellationToken cancellationToken = default);
}
