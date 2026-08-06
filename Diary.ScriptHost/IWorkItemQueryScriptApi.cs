using System.Collections.Immutable;

namespace Diary.ScriptHost;

public enum ScriptWorkItemTagFilter
{
    Ignore = 0,
    Any = 1,
    All = 2,
    None = 3,
    Exact = 4,
}

public sealed record ScriptWorkItemQuery
{
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public ImmutableArray<int> TagIds { get; init; } = ImmutableArray<int>.Empty;
    public ScriptWorkItemTagFilter TagFilter { get; init; }
    public string? Text { get; init; }
    public int? Priority { get; init; }
    public int? Limit { get; init; }
    public int Offset { get; init; }
}

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

public enum ScriptQueryErrorCode
{
    PermissionDenied = 1,
    DatabaseUnavailable = 2,
    InvalidInput = 3,
    ProviderFailure = 4,
    Cancelled = 5,
}

public sealed record ScriptQueryError(ScriptQueryErrorCode Code, string Message);

public sealed record ScriptWorkItemQueryResult(
    bool Succeeded,
    ImmutableArray<ScriptWorkItem> Items,
    ScriptWorkItemQuery? NormalizedQuery,
    ScriptQueryError? Error)
{
    public static ScriptWorkItemQueryResult Success(
        ImmutableArray<ScriptWorkItem> items,
        ScriptWorkItemQuery normalizedQuery) =>
        new(true, items, normalizedQuery, null);

    public static ScriptWorkItemQueryResult Failure(ScriptQueryErrorCode code, string message) =>
        new(false, ImmutableArray<ScriptWorkItem>.Empty, null, new ScriptQueryError(code, message));
}

public interface IWorkItemQueryScriptApi
{
    ValueTask<ScriptWorkItemQueryResult> QueryAsync(
        ScriptWorkItemQuery query,
        CancellationToken cancellationToken = default);
}
