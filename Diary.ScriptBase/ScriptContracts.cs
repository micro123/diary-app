using System.Collections.Immutable;
using System.Globalization;
using Diary.Core.Data.Base;

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

public enum ScriptEntryKind
{
    Application = 1,
    Editor = 2,
    Automation = 3,
    Query = 4,
}

public enum ScriptAutomationTriggerKind
{
    Unknown = 0,
    Startup = 1,
    Scheduled = 2,
    WorkItemCreated = 3,
    WorkItemSaved = 4,
    TagAdded = 5,
}

public enum ScriptEditorTargetKind
{
    Year = 1,
    Quarter = 2,
    Month = 3,
    Day = 4,
    WorkItem = 5,
    Week = 6,
}

public enum ScriptExecutionSource
{
    Unknown = 0,
    Manual = 1,
    Editor = 2,
    Startup = 3,
    Automation = 4,
    WorkItemCreated = 5,
    WorkItemSaved = 6,
    TagAdded = 7,
}

public sealed record ScriptDescriptor(
    string Id,
    string Name,
    ScriptApiVersion ApiVersion,
    ScriptScope Scope,
    string? Description = null,
    IReadOnlyList<ScriptEditorTargetKind>? SupportedEditorTargets = null,
    ScriptEntryKind? EntryKind = null);

public sealed record ScriptDescriptorHint(
    string? Id = null,
    string? Name = null,
    ScriptScope? Scope = null,
    string? Description = null,
    string? EngineName = null,
    IReadOnlyList<ScriptEditorTargetKind>? SupportedEditorTargets = null,
    ScriptEntryKind? EntryKind = null);

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

public enum ScriptErrorCategory
{
    Validation = 1,
    Permission = 2,
    Host = 3,
    Provider = 4,
    Cancellation = 5,
    Conflict = 6,
    Runtime = 7,
}

public sealed record ScriptApiError(
    string Code,
    string Message,
    ScriptErrorCategory Category,
    bool Retryable = false,
    IReadOnlyDictionary<string, object?>? Details = null);

public static class ScriptApiErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string ProviderFailure = "PROVIDER_FAILURE";
    public const string InstanceUnavailable = "INSTANCE_UNAVAILABLE";
    public const string ApiUnavailable = "SCRIPT_API_UNAVAILABLE";
    public const string ApiScopeNotSupported = "SCRIPT_API_SCOPE_NOT_SUPPORTED";
    public const string HostNotConfigured = "SCRIPT_API_HOST_NOT_CONFIGURED";
    public const string Cancelled = "CANCELLED";
    public const string Timeout = "TIMEOUT";
    public const string WorkerTerminated = "WORKER_TERMINATED";
    public const string DuplicateRequest = "DUPLICATE_REQUEST";
}

public sealed record ScriptEffectSummary(
    int AppendedCount = 0,
    bool Preview = false,
    string? IdempotencyKey = null,
    IReadOnlyCollection<int>? CreatedWorkItemIds = null,
    IReadOnlyCollection<string>? RemoteEffects = null);

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
    bool Disabled)
{
    public bool IsPrimary => Level == 0;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record ScriptWorkItem(
    int Id,
    string Date,
    string Comment,
    double Hours,
    int Priority,
    string? Note,
    ImmutableArray<ScriptWorkTag> Tags)
{
    public ImmutableArray<ScriptWorkItemExtraField> ExtraFields { get; init; } =
        ImmutableArray<ScriptWorkItemExtraField>.Empty;

    public ScriptWorkItemExtraField? GetExtraField(string fieldKey) =>
        ExtraFields.FirstOrDefault(field =>
            string.Equals(field.FieldKey, fieldKey, StringComparison.OrdinalIgnoreCase));

    public string? GetExtraFieldValue(string fieldKey) => GetExtraField(fieldKey)?.Value;
}

public sealed record ScriptWorkItemExtraField(
    string FieldId,
    string FieldKey,
    int TagId,
    string TagName,
    string Label,
    TagExtraFieldType Type,
    string Value);

public sealed record ScriptEditorTarget(
    ScriptEditorTargetKind Kind,
    int? Year = null,
    int? Quarter = null,
    int? Month = null,
    string? Date = null,
    ScriptWorkItem? WorkItem = null,
    string? WeekStart = null)
{
    public static ScriptEditorTarget ForYear(int year) => new(ScriptEditorTargetKind.Year, Year: year);

    public static ScriptEditorTarget ForQuarter(int year, int quarter) =>
        new(ScriptEditorTargetKind.Quarter, Year: year, Quarter: quarter);

    public static ScriptEditorTarget ForMonth(int year, int month) =>
        new(ScriptEditorTargetKind.Month, Year: year, Month: month);

    public static ScriptEditorTarget ForDay(string date) => new(ScriptEditorTargetKind.Day, Date: date);

    public static ScriptEditorTarget ForWorkItem(ScriptWorkItem workItem) =>
        new(ScriptEditorTargetKind.WorkItem, WorkItem: workItem);

    public static ScriptEditorTarget ForWeek(string weekStartDate) =>
        new(ScriptEditorTargetKind.Week, WeekStart: weekStartDate);
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
            case ScriptEditorTargetKind.Week when target.WeekStart is { } weekStart:
                if (!DateOnly.TryParseExact(weekStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var weekDay))
                    return Fail("周目标参数无效。", out error);
                if (weekDay.DayOfWeek != DayOfWeek.Monday)
                    return Fail("周目标起始日期必须是周一。", out error);
                if (HasUnexpectedFields(target, weekStart: true))
                    return Fail("周目标参数无效。", out error);
                range = new ScriptDateRange(
                    weekStart,
                    weekDay.AddDays(6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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
        bool workItem = false,
        bool weekStart = false) =>
        (!year && target.Year is not null)
        || (!quarter && target.Quarter is not null)
        || (!month && target.Month is not null)
        || (!date && target.Date is not null)
        || (!workItem && target.WorkItem is not null)
        || (!weekStart && target.WeekStart is not null);

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

public sealed record ScriptExecutionRequest(
    ScriptEditorTarget? Target = null,
    ImmutableDictionary<string, string>? Arguments = null,
    ScriptExecutionSource Source = ScriptExecutionSource.Unknown,
    ScriptEntryKind? EntryKind = null,
    string? IdempotencyKey = null,
    bool Preview = false);

public sealed record ScriptProgressUpdate(
    double Fraction,
    string Message);

public sealed record ScriptAutomationContext(
    ScriptAutomationTriggerKind Trigger,
    IReadOnlyDictionary<string, string> EventData,
    string? IdempotencyKey = null);

public sealed record ScriptExecutionMetadata(
    Guid ExecutionId,
    DateTimeOffset StartedAt,
    ScriptExecutionSource Source,
    string ScriptId,
    ScriptEntryKind EntryKind = ScriptEntryKind.Application,
    string? IdempotencyKey = null,
    bool Preview = false);

public sealed record ScriptExecutionResult(
    ScriptExecutionStatus Status,
    ImmutableArray<ScriptDiagnostic> Diagnostics,
    ScriptEffectSummary? Effects = null)
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

    ScriptEntryKind EntryKind { get; }

    IReadOnlyDictionary<string, string> Arguments { get; }

    CancellationToken CancellationToken { get; }

    ValueTask ReportProgressAsync(ScriptProgressUpdate update);

    TApi? GetApi<TApi>() where TApi : class;

    TApi GetRequiredApi<TApi>() where TApi : class;

    bool IsCancellationRequested { get; }
}

public interface IScriptApplicationContext : IScriptExecutionContext
{

}

public interface IScriptEditorContext : IScriptExecutionContext
{
    ScriptEditorTarget Target { get; }
    ScriptWorkItem? WorkItem { get; }

    ScriptDateRange? GetDateRange();
    IAsyncEnumerable<ScriptWorkItem> StreamItemsAsync(
        CancellationToken cancellationToken = default);
}

public interface IScriptAutomationContext : IScriptExecutionContext
{
    ScriptAutomationContext Automation { get; }


}

public interface IScriptProgramV1
{
    ScriptDescriptor Descriptor { get; }

    ValueTask<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        CancellationToken cancellationToken = default);
}

public interface IApplicationScriptV1
{
    ScriptDescriptor Descriptor { get; }

    ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptApplicationContext context,
        CancellationToken cancellationToken = default);
}

public interface IEditorScriptV1
{
    ScriptDescriptor Descriptor { get; }

    ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptEditorContext context,
        CancellationToken cancellationToken = default);
}

public interface IAutomationScriptV1
{
    ScriptDescriptor Descriptor { get; }

    ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptAutomationContext context,
        CancellationToken cancellationToken = default);
}

public interface IQueryScriptV1
{
    ScriptDescriptor Descriptor { get; }

    ValueTask<ScriptExecutionResult> ExecuteAsync(
        IScriptApplicationContext context,
        CancellationToken cancellationToken = default);
}

public static class ScriptProgramAdapter
{
    public static bool TryAdapt(object program, out IScriptProgramV1? adapted)
    {
        ArgumentNullException.ThrowIfNull(program);
        adapted = program switch
        {
            IScriptProgramV1 v1 => v1,
            IApplicationScriptV1 application => new TypedScriptProgramAdapter(application),
            IEditorScriptV1 editor => new TypedScriptProgramAdapter(editor),
            IAutomationScriptV1 automation => new TypedScriptProgramAdapter(automation),
            IQueryScriptV1 query => new TypedScriptProgramAdapter(query),
            _ => null,
        };
        return adapted is not null;
    }

    private sealed class TypedScriptProgramAdapter : IScriptProgramV1, IDisposable
    {
        private readonly object _program;

        public TypedScriptProgramAdapter(object program)
        {
            _program = program;
            Descriptor = program switch
            {
                IApplicationScriptV1 application => application.Descriptor,
                IEditorScriptV1 editor => editor.Descriptor,
                IAutomationScriptV1 automation => automation.Descriptor,
                IQueryScriptV1 query => query.Descriptor,
                _ => throw new ArgumentException("Unsupported typed script program.", nameof(program)),
            };
        }

        public ScriptDescriptor Descriptor { get; }

        public ValueTask<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var entryKind = ScriptEntryKindResolver.Resolve(request, Descriptor);
            return entryKind switch
            {
                ScriptEntryKind.Application when _program is IApplicationScriptV1 application
                    && context is IScriptApplicationContext applicationContext =>
                    application.ExecuteAsync(applicationContext, cancellationToken),
                ScriptEntryKind.Editor when _program is IEditorScriptV1 editor
                    && context is IScriptEditorContext editorContext =>
                    editor.ExecuteAsync(editorContext, cancellationToken),
                ScriptEntryKind.Automation when _program is IAutomationScriptV1 automation
                    && context is IScriptAutomationContext automationContext =>
                    automation.ExecuteAsync(automationContext, cancellationToken),
                ScriptEntryKind.Query when _program is IQueryScriptV1 query
                    && context is IScriptApplicationContext queryContext =>
                    query.ExecuteAsync(queryContext, cancellationToken),
                _ => ValueTask.FromResult(new ScriptExecutionResult(
                    ScriptExecutionStatus.Rejected,
                    [new ScriptDiagnostic(
                        "SCRIPT_ENTRY_CONTEXT_MISMATCH",
                        "The script entry point and execution context do not match.",
                        ScriptDiagnosticSeverity.Error,
                        ScriptDiagnosticCategory.Validation)])),
            };
        }

        public void Dispose()
        {
            if (_program is IDisposable disposable)
                disposable.Dispose();
        }
    }
}

public static class ScriptEntryKindResolver
{
    public static ScriptEntryKind Resolve(ScriptDescriptor descriptor) =>
        descriptor.EntryKind ?? (descriptor.Scope == ScriptScope.Editor
            ? ScriptEntryKind.Editor
            : ScriptEntryKind.Application);

    public static ScriptEntryKind Resolve(ScriptExecutionRequest request, ScriptDescriptor descriptor) =>
        request.EntryKind ?? Resolve(descriptor);

    public static bool IsCompatible(ScriptEntryKind entryKind, ScriptScope scope) =>
        entryKind switch
        {
            ScriptEntryKind.Editor => scope == ScriptScope.Editor,
            ScriptEntryKind.Application or ScriptEntryKind.Automation or ScriptEntryKind.Query =>
                scope == ScriptScope.Application,
            _ => false,
        };

    public static bool RequiresEditorTarget(ScriptEntryKind entryKind) =>
        entryKind == ScriptEntryKind.Editor;
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
