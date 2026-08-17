using System.Collections.Immutable;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class WorkItemQueryScriptApi(
    Func<DbInterfaceBase?> databaseProvider) : IWorkItemQueryScriptApi
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1_000;
    public const int MaxStreamPageSize = 500;
    public const int MaxOffset = 1_000_000;
    public const int MaxTagCount = 100;

    public ValueTask<ScriptWorkItemQueryResult> QueryAsync(
        ScriptWorkItemQuery query,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(Failure(ScriptQueryErrorCode.Cancelled, "查询已取消。"));
        if (!TryNormalize(query, out var normalized, out var databaseQuery, out var validationError))
            return ValueTask.FromResult(Failure(ScriptQueryErrorCode.InvalidInput, validationError));

        DbInterfaceBase? database;
        try
        {
            database = databaseProvider();
        }
        catch (Exception)
        {
            return ValueTask.FromResult(Failure(ScriptQueryErrorCode.ProviderFailure, "数据库提供程序不可用。"));
        }
        if (database is null)
            return ValueTask.FromResult(Failure(ScriptQueryErrorCode.DatabaseUnavailable, "数据库尚未连接。"));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workItems = database.QueryWorkItems(databaseQuery).ToArray();
            cancellationToken.ThrowIfCancellationRequested();

            cancellationToken.ThrowIfCancellationRequested();
            var workItemIds = workItems.Select(item => item.Id).ToArray();
            var notes = database.GetWorkNotesByWorkItemIds(workItemIds);
            cancellationToken.ThrowIfCancellationRequested();
            var tags = database.GetWorkTagsByWorkItemIds(workItemIds);
            cancellationToken.ThrowIfCancellationRequested();
            var extraFields = database.GetWorkItemExtraFieldsByWorkItemIds(workItemIds);
            cancellationToken.ThrowIfCancellationRequested();

            var result = workItems.Select(item => new ScriptWorkItem(
                item.Id,
                item.CreateDate,
                item.Comment,
                item.Time,
                (int)item.Priority,
                notes.GetValueOrDefault(item.Id),
                tags.TryGetValue(item.Id, out var itemTags)
                    ? [.. itemTags.Select(tag => new ScriptWorkTag(
                        tag.Id,
                        tag.Name,
                        tag.Color,
                        (int)tag.Level,
                        tag.Disabled)
                    { Metadata = new Dictionary<string, string>(tag.Metadata, StringComparer.Ordinal) })]
                    : ImmutableArray<ScriptWorkTag>.Empty)
            {
                ExtraFields = extraFields.TryGetValue(item.Id, out var itemFields)
                        ? [.. itemFields.Select(field => new ScriptWorkItemExtraField(
                            field.FieldId, field.FieldKey, field.TagId, field.TagName,
                            field.Label, field.Type, field.Value))]
                        : ImmutableArray<ScriptWorkItemExtraField>.Empty,
            }).ToImmutableArray();
            return ValueTask.FromResult(ScriptWorkItemQueryResult.Success(result, normalized));
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromResult(Failure(ScriptQueryErrorCode.Cancelled, "查询已取消。"));
        }
        catch (Exception)
        {
            return ValueTask.FromResult(Failure(ScriptQueryErrorCode.ProviderFailure, "数据库查询失败。"));
        }
    }

    private static bool TryNormalize(
        ScriptWorkItemQuery? query,
        out ScriptWorkItemQuery normalized,
        out WorkItemQuery databaseQuery,
        out string error)
    {
        normalized = new ScriptWorkItemQuery();
        databaseQuery = new WorkItemQuery();
        if (query is null)
            return Fail("查询条件不能为空。", out error);

        if (query.Range is not null)
        {
            if (!TryResolveRange(query.Range, out var rangeStart, out var rangeEnd, out error))
                return false;
            query = query with { StartDate = rangeStart, EndDate = rangeEnd };
        }

        var tagIds = query.TagIds.IsDefault ? ImmutableArray<int>.Empty : query.TagIds;
        if (tagIds.Length > MaxTagCount)
            return Fail($"标签数量不能超过 {MaxTagCount} 个。", out error);
        if (query.Limit is <= 0 or > MaxLimit)
            return Fail($"结果上限必须在 1 到 {MaxLimit} 之间。", out error);
        if (query.Offset is < 0 or > MaxOffset)
            return Fail($"结果偏移必须在 0 到 {MaxOffset} 之间。", out error);
        if (!Enum.IsDefined(query.TagFilter))
            return Fail("标签筛选模式无效。", out error);
        if (query.Priority is < 0 or > 9)
            return Fail("优先级无效。", out error);

        databaseQuery = new WorkItemQuery
        {
            StartDate = query.StartDate,
            EndDate = query.EndDate,
            TagIds = tagIds,
            TagFilter = (WorkItemTagFilter)query.TagFilter,
            Text = query.Text,
            Priority = query.Priority is null ? null : (WorkPriorities)query.Priority.Value,
            Limit = query.Limit ?? DefaultLimit,
            Offset = query.Offset,
        };
        if (!WorkItemQueryNormalizer.TryNormalize(databaseQuery, out databaseQuery, out error))
            return false;

        normalized = new ScriptWorkItemQuery
        {
            StartDate = databaseQuery.StartDate,
            EndDate = databaseQuery.EndDate,
            TagIds = [.. databaseQuery.TagIds],
            TagFilter = (ScriptWorkItemTagFilter)databaseQuery.TagFilter,
            Text = databaseQuery.Text,
            Priority = databaseQuery.Priority is null ? null : (int)databaseQuery.Priority.Value,
            Limit = databaseQuery.Limit,
            Offset = databaseQuery.Offset,
        };
        return true;
    }

    private static bool TryResolveRange(string range, out string startDate, out string endDate, out string error)
    {
        startDate = endDate = string.Empty;
        error = string.Empty;
        var today = DateTime.Today;
        switch (range.Trim().ToLowerInvariant())
        {
            case "today":
                startDate = FormatDate(today);
                endDate = startDate;
                return true;
            case "yesterday":
                startDate = FormatDate(today.AddDays(-1));
                endDate = startDate;
                return true;
            case "thisweek":
                {
                    var start = StartOfWeek(today);
                    startDate = FormatDate(start);
                    endDate = FormatDate(start.AddDays(6));
                    return true;
                }
            case "thismonth":
                {
                    var start = new DateTime(today.Year, today.Month, 1);
                    startDate = FormatDate(start);
                    endDate = FormatDate(start.AddMonths(1).AddDays(-1));
                    return true;
                }
            default:
                return Fail("日期范围快捷值无效，支持 today、yesterday、thisWeek、thisMonth。", out error);
        }
    }

    private static string FormatDate(DateTime date) =>
        date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime StartOfWeek(DateTime date)
    {
        var day = (int)date.DayOfWeek;
        if (day == 0)
            day = 7;
        return date.Date.AddDays(-day + 1);
    }

    private static ScriptWorkItemQueryResult Failure(ScriptQueryErrorCode code, string message) =>
        ScriptWorkItemQueryResult.Failure(code, message);

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
