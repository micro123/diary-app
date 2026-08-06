using System.Globalization;

namespace Diary.Core.Data.Base;

public enum WorkItemTagFilter
{
    Ignore,
    Any,
    All,
    None,
    Exact,
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

public static class WorkItemQueryNormalizer
{
    public const int MaxTagCount = 500;
    public const int MaxLimit = 10_000;

    public static WorkItemQuery Normalize(WorkItemQuery query)
    {
        if (!TryNormalize(query, out var normalized, out var error))
            throw new ArgumentException(error, nameof(query));
        return normalized;
    }

    public static bool TryNormalize(
        WorkItemQuery? query,
        out WorkItemQuery normalized,
        out string error)
    {
        normalized = new WorkItemQuery();
        if (query is null)
            return Fail("查询条件不能为空", out error);
        if (!Enum.IsDefined(query.TagFilter))
            return Fail("标签筛选模式无效", out error);
        if (query.Priority is not null && !Enum.IsDefined(query.Priority.Value))
            return Fail("优先级无效", out error);
        if (query.TagIds is null)
            return Fail("标签列表不能为空", out error);

        var tagIds = query.TagIds.Distinct().ToArray();
        if (tagIds.Length > MaxTagCount)
            return Fail($"标签数量不能超过 {MaxTagCount} 个", out error);
        if (tagIds.Any(id => id <= 0))
            return Fail("标签 ID 必须为正整数", out error);
        if (query.TagFilter is WorkItemTagFilter.Any or WorkItemTagFilter.All && tagIds.Length == 0)
            return Fail("任意标签或全部标签模式至少需要一个标签", out error);
        if (query.Limit is <= 0 or > MaxLimit)
            return Fail($"结果上限必须在 1 到 {MaxLimit} 之间", out error);
        if (query.Offset < 0)
            return Fail("结果偏移不能为负数", out error);
        if (query.Offset > 0 && query.Limit is null)
            return Fail("设置结果偏移时必须同时设置结果上限", out error);
        if (!TryNormalizeDate(query.StartDate, "开始日期", out var startDate, out error)
            || !TryNormalizeDate(query.EndDate, "结束日期", out var endDate, out error))
            return false;
        if (startDate is not null && endDate is not null
            && string.CompareOrdinal(startDate, endDate) > 0)
            return Fail("开始日期不能晚于结束日期", out error);

        if (query.TagFilter is WorkItemTagFilter.Ignore or WorkItemTagFilter.None)
            tagIds = Array.Empty<int>();
        normalized = query with
        {
            StartDate = startDate,
            EndDate = endDate,
            TagIds = tagIds,
            Text = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim(),
        };
        error = string.Empty;
        return true;
    }

    private static bool TryNormalizeDate(
        string? value,
        string fieldName,
        out string? normalized,
        out string error)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized is null)
        {
            error = string.Empty;
            return true;
        }
        if (!DateOnly.TryParseExact(normalized, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            return Fail($"{fieldName}必须使用 yyyy-MM-dd 格式", out error);
        error = string.Empty;
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
