namespace Diary.Core.Data.Base;

public enum WorkItemTagFilter
{
    Ignore,
    Any,
    All,
    None,
}

public sealed record WorkItemQuery
{
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public IReadOnlyCollection<int> TagIds { get; init; } = Array.Empty<int>();
    public WorkItemTagFilter TagFilter { get; init; } = WorkItemTagFilter.Ignore;
    public string? Text { get; init; }
    public WorkPriorities? Priority { get; init; }
    public int? Limit { get; init; }
    public int Offset { get; init; }
}
