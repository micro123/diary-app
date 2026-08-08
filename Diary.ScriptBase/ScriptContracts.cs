using System.Collections.Immutable;
using System.Globalization;

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

public enum ScriptEditorTargetKind
{
    Year = 1,
    Quarter = 2,
    Month = 3,
    Day = 4,
    WorkItem = 5,
}

public enum ScriptExecutionSource
{
    Unknown = 0,
    Manual = 1,
    Editor = 2,
    Startup = 3,
    Automation = 4,
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

public sealed record ScriptDateRange(string StartDate, string EndDate);

public sealed record ScriptWorkTag(
    int Id,
    string Name,
    int Color,
    int Level,
    bool Disabled);

public sealed record ScriptWorkItem(
    int Id,
    string Date,
    string Comment,
    double Hours,
    int Priority,
    string? Note,
    ImmutableArray<ScriptWorkTag> Tags);

public sealed record ScriptEditorTarget(
    ScriptEditorTargetKind Kind,
    int? Year = null,
    int? Quarter = null,
    int? Month = null,
    string? Date = null,
    ScriptWorkItem? WorkItem = null)
{
    public static ScriptEditorTarget ForYear(int year) => new(ScriptEditorTargetKind.Year, Year: year);

    public static ScriptEditorTarget ForQuarter(int year, int quarter) =>
        new(ScriptEditorTargetKind.Quarter, Year: year, Quarter: quarter);

    public static ScriptEditorTarget ForMonth(int year, int month) =>
        new(ScriptEditorTargetKind.Month, Year: year, Month: month);

    public static ScriptEditorTarget ForDay(string date) => new(ScriptEditorTargetKind.Day, Date: date);

    public static ScriptEditorTarget ForWorkItem(ScriptWorkItem workItem) =>
        new(ScriptEditorTargetKind.WorkItem, WorkItem: workItem);
}

public static class ScriptEditorTargetResolver
{
    public static bool TryValidate(
        ScriptEditorTarget? target,
        out ScriptDateRange? range,
        out string error)
    {
        range = null;
        error = string.Empty;
        if (target is null)
            return Fail("编辑器脚本必须提供目标。", out error);

        switch (target.Kind)
        {
            case ScriptEditorTargetKind.Year when target.Year is { } year:
                if (year is < 1 or > 9999)
                    return Fail("年份无效。", out error);
                if (HasUnexpectedFields(target, year: true))
                    return Fail("年度目标参数无效。", out error);
                range = new ScriptDateRange(
                    new DateOnly(year, 1, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    new DateOnly(year, 12, 31).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;
            case ScriptEditorTargetKind.Quarter when target.Year is { } quarterYear && target.Quarter is { } quarter:
                if (quarterYear is < 1 or > 9999 || quarter is < 1 or > 4)
                    return Fail("季度目标参数无效。", out error);
                if (HasUnexpectedFields(target, year: true, quarter: true))
                    return Fail("季度目标参数无效。", out error);
                var startMonth = (quarter - 1) * 3 + 1;
                range = new ScriptDateRange(
                    new DateOnly(quarterYear, startMonth, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    new DateOnly(quarterYear, startMonth + 2, DateTime.DaysInMonth(quarterYear, startMonth + 2))
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;
            case ScriptEditorTargetKind.Month when target.Year is { } monthYear && target.Month is { } month:
                if (monthYear is < 1 or > 9999 || month is < 1 or > 12)
                    return Fail("月份目标参数无效。", out error);
                if (HasUnexpectedFields(target, year: true, month: true))
                    return Fail("月份目标参数无效。", out error);
                range = new ScriptDateRange(
                    new DateOnly(monthYear, month, 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    new DateOnly(monthYear, month, DateTime.DaysInMonth(monthYear, month))
                        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;
            case ScriptEditorTargetKind.Day when target.Date is { } date:
                if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    return Fail("日期目标参数无效。", out error);
                if (HasUnexpectedFields(target, date: true))
                    return Fail("日期目标参数无效。", out error);
                range = new ScriptDateRange(date, date);
                return true;
            case ScriptEditorTargetKind.WorkItem when target.WorkItem is { Id: > 0 }:
                if (HasUnexpectedFields(target, workItem: true))
                    return Fail("事项目标参数无效。", out error);
                return true;
            default:
                return Fail("编辑器目标参数不完整或类型无效。", out error);
        }
    }

    public static ScriptDateRange? GetDateRange(ScriptEditorTarget target)
    {
        if (!TryValidate(target, out var range, out var error))
            throw new ArgumentException(error, nameof(target));
        return range;
    }

    private static bool HasUnexpectedFields(
        ScriptEditorTarget target,
        bool year = false,
        bool quarter = false,
        bool month = false,
        bool date = false,
        bool workItem = false) =>
        (!year && target.Year is not null)
        || (!quarter && target.Quarter is not null)
        || (!month && target.Month is not null)
        || (!date && target.Date is not null)
        || (!workItem && target.WorkItem is not null);

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

public sealed record ScriptExecutionRequest(
    ScriptEditorTarget? Target = null,
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

public interface IScriptApplicationContext : IScriptExecutionContext
{
}

public interface IScriptEditorContext : IScriptExecutionContext
{
    ScriptEditorTarget Target { get; }
    ScriptWorkItem? WorkItem { get; }
    IReadOnlyDictionary<string, string> Arguments { get; }
    ScriptDateRange? GetDateRange();
    IAsyncEnumerable<ScriptWorkItem> StreamItemsAsync(
        CancellationToken cancellationToken = default);
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
