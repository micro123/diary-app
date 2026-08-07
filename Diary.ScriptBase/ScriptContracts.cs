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

public enum ScriptScope
{
    Application = 1,
    Editor = 2,
}

public enum ScriptExecutionSource
{
    Unknown = 0,
    Manual = 1,
    Editor = 2,
    Startup = 3,
    Automation = 4,
}

public enum ScriptTimeGranularity
{
    Custom = 0,
    Day = 1,
    Week = 2,
    Month = 3,
    Quarter = 4,
    Year = 5,
}

public enum ScriptBusinessTargetKind
{
    Diary = 1,
    WorkItem = 2,
    Project = 3,
    TrackerIssue = 4,
    TrackerInstance = 5,
}

public sealed record ScriptDescriptor(
    string Id,
    string Name,
    ScriptApiVersion ApiVersion,
    ScriptScope Scope,
    string? Description = null);

public sealed record ScriptDescriptorHint(
    string? Id = null,
    string? Name = null,
    ScriptScope? Scope = null,
    string? Description = null,
    string? EngineName = null);

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
    ScriptApiVersion ApiVersion = ScriptApiVersion.V1,
    ScriptDescriptorHint? DescriptorHint = null);

public sealed record ScriptBuildResult(
    bool Succeeded,
    IScriptProgramV1? Program,
    ImmutableArray<ScriptDiagnostic> Diagnostics)
{
    public string? EngineName { get; init; }

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

public sealed record EditorScriptContext(
    string StartDate,
    string EndDate,
    ScriptTimeGranularity Granularity = ScriptTimeGranularity.Custom);

public sealed record ScriptBusinessTarget(
    ScriptBusinessTargetKind Kind,
    string TargetId,
    string? PluginId = null,
    string? InstanceId = null);

public sealed record ScriptTarget(
    ScriptScope Scope,
    EditorScriptContext? Editor = null,
    ScriptBusinessTarget? Business = null);

public sealed record ScriptExecutionRequest(
    ScriptTarget Target,
    ImmutableDictionary<string, string>? Arguments = null,
    ScriptExecutionSource Source = ScriptExecutionSource.Unknown);

public sealed record ScriptExecutionMetadata(
    Guid ExecutionId,
    DateTimeOffset StartedAt,
    ScriptExecutionSource Source,
    string ScriptId);

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
    ScriptExecutionMetadata? Metadata { get; }

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
