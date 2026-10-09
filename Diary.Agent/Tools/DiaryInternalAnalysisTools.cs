using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.Agent.Tools;

public sealed class ListExtraFieldsTool(ITagExtraFieldScriptApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.ListExtraFields;

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var includeDisabled = arguments.TryGetProperty("includeDisabled", out var disabled)
                                  && disabled.GetBoolean();
            var tagIds = arguments.TryGetProperty("tagIds", out var tags)
                ? tags.EnumerateArray().Select(item => item.GetInt32()).ToHashSet()
                : null;
            var fields = api.List(includeDisabled)
                .Where(field => tagIds is null || tagIds.Contains(field.TagId))
                .ToArray();
            return ValueTask.FromResult(AgentToolResult.Success(
                JsonSerializer.Serialize(new { fields, count = fields.Length }),
                "DiaryApp"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return ValueTask.FromResult(AgentToolResult.Failure("invalid_arguments", "附加字段查询参数无效。"));
        }
    }
}

public sealed class AnalyzeWorkLogQualityTool(IWorkItemQueryScriptApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.AnalyzeWorkLogQuality;

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!WorkItemAnalysis.TryParseRange(arguments, out var range, out var error))
            return AgentToolResult.Failure("invalid_arguments", error);
        var loaded = await WorkItemAnalysis.LoadAsync(api, range, cancellationToken);
        if (!loaded.Succeeded)
            return loaded.Error!;
        var items = loaded.Items;
        var expectedHours = arguments.TryGetProperty("expectedDailyHours", out var expected)
            ? expected.GetDouble()
            : 8d;
        var includeWeekends = arguments.TryGetProperty("includeWeekends", out var weekends)
                              && weekends.GetBoolean();
        var days = WorkItemAnalysis.EnumerateDates(range.Start, range.End)
            .Where(date => includeWeekends || date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .ToArray();
        var byDate = items.GroupBy(item => item.Date).ToDictionary(group => group.Key);
        var missingDates = days
            .Where(date => !byDate.ContainsKey(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            .Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToArray();
        var dailyAnomalies = byDate.OrderBy(pair => pair.Key)
            .Select(pair => new
            {
                date = pair.Key,
                hours = pair.Value.Sum(item => item.Hours),
                count = pair.Value.Count(),
            })
            .Where(day => day.hours < expectedHours || day.hours > 16)
            .ToArray();
        var duplicates = items
            .GroupBy(item => new { item.Date, Title = item.Comment.Trim().ToUpperInvariant() })
            .Where(group => group.Count() > 1)
            .Select(group => new
            {
                date = group.Key.Date,
                title = group.First().Comment,
                workItemIds = group.Select(item => item.Id).Order().ToArray(),
            })
            .ToArray();
        return AgentToolResult.Success(JsonSerializer.Serialize(new
        {
            range = new { startDate = range.StartText, endDate = range.EndText },
            loadedCount = items.Count,
            loaded.Truncated,
            missingDates,
            dailyAnomalies,
            zeroOrNegativeHours = items.Where(item => item.Hours <= 0).Select(item => item.Id).ToArray(),
            untaggedWorkItemIds = items.Where(item => item.Tags.Length == 0).Select(item => item.Id).ToArray(),
            disabledTagReferences = items
                .SelectMany(item => item.Tags.Where(tag => tag.Disabled).Select(tag => new { workItemId = item.Id, tag.Id, tag.Name }))
                .ToArray(),
            duplicates,
        }), "DiaryApp");
    }
}

public sealed class CompareWorkPeriodsTool(IWorkItemQueryScriptApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.CompareWorkPeriods;

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        var leftValid = WorkItemAnalysis.TryParseNamedRange(arguments, "left", out var left, out var leftError);
        var rightValid = WorkItemAnalysis.TryParseNamedRange(arguments, "right", out var right, out var rightError);
        if (!leftValid || !rightValid)
        {
            return AgentToolResult.Failure("invalid_arguments", leftError ?? rightError ?? "周期参数无效。");
        }
        var leftItems = await WorkItemAnalysis.LoadAsync(api, left, cancellationToken);
        if (!leftItems.Succeeded)
            return leftItems.Error!;
        var rightItems = await WorkItemAnalysis.LoadAsync(api, right, cancellationToken);
        if (!rightItems.Succeeded)
            return rightItems.Error!;
        var leftSummary = WorkItemAnalysis.Summarize(leftItems.Items);
        var rightSummary = WorkItemAnalysis.Summarize(rightItems.Items);
        return AgentToolResult.Success(JsonSerializer.Serialize(new
        {
            left = new { range = new { startDate = left.StartText, endDate = left.EndText }, summary = leftSummary, leftItems.Truncated },
            right = new { range = new { startDate = right.StartText, endDate = right.EndText }, summary = rightSummary, rightItems.Truncated },
            delta = new
            {
                count = rightSummary.Count - leftSummary.Count,
                totalHours = rightSummary.TotalHours - leftSummary.TotalHours,
                averageDailyHours = rightSummary.AverageDailyHours - leftSummary.AverageDailyHours,
            },
        }), "DiaryApp");
    }
}

public sealed class GetCalendarOverviewTool(IWorkItemQueryScriptApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.GetCalendarOverview;

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!WorkItemAnalysis.TryParseRange(arguments, out var range, out var error))
            return AgentToolResult.Failure("invalid_arguments", error);
        var loaded = await WorkItemAnalysis.LoadAsync(api, range, cancellationToken);
        if (!loaded.Succeeded)
            return loaded.Error!;
        var includeWeekends = arguments.TryGetProperty("includeWeekends", out var weekends)
                              && weekends.GetBoolean();
        var groups = loaded.Items.GroupBy(item => item.Date).ToDictionary(group => group.Key);
        var days = WorkItemAnalysis.EnumerateDates(range.Start, range.End)
            .Where(date => includeWeekends || date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .Select(date =>
            {
                var text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var items = groups.TryGetValue(text, out var group) ? group.ToArray() : [];
                return new { date = text, count = items.Length, hours = items.Sum(item => item.Hours), hasRecords = items.Length > 0 };
            })
            .ToArray();
        return AgentToolResult.Success(JsonSerializer.Serialize(new
        {
            range = new { startDate = range.StartText, endDate = range.EndText },
            days,
            recordedDays = days.Count(day => day.hasRecords),
            missingDays = days.Count(day => !day.hasRecords),
            totalHours = days.Sum(day => day.hours),
            loaded.Truncated,
        }), "DiaryApp");
    }
}

internal static class WorkItemAnalysis
{
    private const int MaxItems = 2_000;
    private const int MaxDays = 366;

    internal static bool TryParseRange(JsonElement arguments, out DateRange range, out string error) =>
        TryParseRangeValues(
            arguments.TryGetProperty("startDate", out var start) ? start.GetString() : null,
            arguments.TryGetProperty("endDate", out var end) ? end.GetString() : null,
            out range,
            out error);

    internal static bool TryParseNamedRange(
        JsonElement arguments,
        string prefix,
        out DateRange range,
        out string? error)
    {
        var startName = prefix + "StartDate";
        var endName = prefix + "EndDate";
        var succeeded = TryParseRangeValues(
            arguments.TryGetProperty(startName, out var start) ? start.GetString() : null,
            arguments.TryGetProperty(endName, out var end) ? end.GetString() : null,
            out range,
            out var message);
        error = succeeded ? null : message;
        return succeeded;
    }

    internal static async ValueTask<LoadedItems> LoadAsync(
        IWorkItemQueryScriptApi api,
        DateRange range,
        CancellationToken cancellationToken)
    {
        var items = new List<ScriptWorkItem>();
        try
        {
            await foreach (var item in api.StreamAsync(new ScriptWorkItemQuery
            {
                StartDate = range.StartText,
                EndDate = range.EndText,
                TagIds = ImmutableArray<int>.Empty,
            }, 200, cancellationToken))
            {
                if (items.Count == MaxItems)
                    return new LoadedItems(items, true, null);
                items.Add(item);
            }
            return new LoadedItems(items, false, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return new LoadedItems([], false, AgentToolResult.Failure("query_failed", exception.Message));
        }
    }

    internal static PeriodSummary Summarize(IReadOnlyCollection<ScriptWorkItem> items)
    {
        var recordedDays = items.Select(item => item.Date).Distinct(StringComparer.Ordinal).Count();
        return new PeriodSummary(
            items.Count,
            items.Sum(item => item.Hours),
            recordedDays == 0 ? 0 : items.Sum(item => item.Hours) / recordedDays,
            items.GroupBy(item => item.Priority).OrderBy(group => group.Key)
                .ToDictionary(group => group.Key, group => new CountHours(group.Count(), group.Sum(item => item.Hours))),
            items.SelectMany(item => item.Tags).GroupBy(tag => new { tag.Id, tag.Name })
                .OrderByDescending(group => group.Count())
                .Select(group => new TagCountHours(
                    group.Key.Id,
                    group.Key.Name,
                    group.Count(),
                    items.Where(item => item.Tags.Any(tag => tag.Id == group.Key.Id)).Sum(item => item.Hours)))
                .ToArray());
    }

    internal static IEnumerable<DateOnly> EnumerateDates(DateOnly start, DateOnly end)
    {
        for (var date = start; date <= end; date = date.AddDays(1))
            yield return date;
    }

    private static bool TryParseRangeValues(
        string? startText,
        string? endText,
        out DateRange range,
        out string error)
    {
        range = default;
        error = string.Empty;
        if (!DateOnly.TryParseExact(startText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(endText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            error = "日期必须是 yyyy-MM-dd 格式。";
            return false;
        }
        if (end < start || end.DayNumber - start.DayNumber + 1 > MaxDays)
        {
            error = $"日期范围必须正向且不能超过 {MaxDays} 天。";
            return false;
        }
        range = new DateRange(start, end, startText!, endText!);
        return true;
    }

    internal readonly record struct DateRange(DateOnly Start, DateOnly End, string StartText, string EndText);
    internal sealed record LoadedItems(IReadOnlyList<ScriptWorkItem> Items, bool Truncated, AgentToolResult? Error)
    {
        public bool Succeeded => Error is null;
    }
    internal sealed record CountHours(int Count, double Hours);
    internal sealed record TagCountHours(int TagId, string TagName, int Count, double Hours);
    internal sealed record PeriodSummary(
        int Count,
        double TotalHours,
        double AverageDailyHours,
        IReadOnlyDictionary<int, CountHours> ByPriority,
        IReadOnlyList<TagCountHours> ByTag);
}
