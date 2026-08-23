using System.Globalization;

namespace Diary.AiContext;

public sealed class AiContextQueryService(AiContextSnapshot snapshot)
{
    public IReadOnlyList<AiContextTag> ListTags()
    {
        EnsureDisclosed(snapshot.Disclosure.Tags, "tags");
        return snapshot.Tags;
    }

    public IReadOnlyList<AiContextExtraFieldDefinition> ListExtraFields()
    {
        EnsureDisclosed(snapshot.Disclosure.ExtraFieldDefinitions, "extra_field_definitions");
        return snapshot.ExtraFieldDefinitions;
    }

    public IReadOnlyList<AiContextTemplate> ListTemplates()
    {
        EnsureDisclosed(snapshot.Disclosure.Templates, "templates");
        return snapshot.Templates;
    }

    public IReadOnlyList<AiContextTrackerInstance> ListTrackerInstances()
    {
        EnsureDisclosed(snapshot.Disclosure.TrackerInstances, "tracker_instances");
        return snapshot.TrackerInstances;
    }

    public IReadOnlyList<AiContextWorkItem> QueryWorkItems(AiContextWorkItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureDisclosed(snapshot.Disclosure.WorkItems, "work_items");
        ValidateQuery(query);
        IEnumerable<AiContextWorkItem> items = snapshot.WorkItems;
        if (!string.IsNullOrWhiteSpace(query.StartDate))
            items = items.Where(item => string.CompareOrdinal(item.Date, query.StartDate) >= 0);
        if (!string.IsNullOrWhiteSpace(query.EndDate))
            items = items.Where(item => string.CompareOrdinal(item.Date, query.EndDate) <= 0);
        if (query.TagIds is { Count: > 0 })
            items = items.Where(item => query.TagIds.All(item.TagIds.Contains));
        if (!string.IsNullOrWhiteSpace(query.Text))
            items = items.Where(item => item.Title.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
                || (item.Note?.Contains(query.Text, StringComparison.OrdinalIgnoreCase) ?? false));
        if (query.Priority is not null)
            items = items.Where(item => item.Priority == query.Priority);
        return items.OrderBy(item => item.Date, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .Skip(query.Offset)
            .Take(query.Limit)
            .ToArray();
    }

    public AiContextWorkItemSummary SummarizeWorkItems(AiContextWorkItemQuery query)
    {
        var items = QueryWorkItems(query with { Limit = AiContextSchema.MaxWorkItems, Offset = 0 });
        var tagsById = snapshot.Tags.ToDictionary(tag => tag.Id);
        var byTag = items.SelectMany(item => item.TagIds.Select(tagId => (Item: item, TagId: tagId)))
            .GroupBy(pair => pair.TagId)
            .Select(group => new AiContextTagSummary(
                group.Key,
                tagsById.TryGetValue(group.Key, out var tag) ? tag.Name : $"#{group.Key}",
                group.Count(),
                group.Sum(pair => pair.Item.Hours)))
            .OrderByDescending(item => item.TotalHours)
            .ThenBy(item => item.TagName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new AiContextWorkItemSummary(items.Count, items.Sum(item => item.Hours), byTag);
    }

    private static void ValidateQuery(AiContextWorkItemQuery query)
    {
        if (query.Limit is < 1 or > AiContextSchema.MaxWorkItems)
            throw new ArgumentOutOfRangeException(nameof(query), $"limit 必须在 1 到 {AiContextSchema.MaxWorkItems} 之间。");
        if (query.Offset is < 0 or > AiContextSchema.MaxQueryOffset)
            throw new ArgumentOutOfRangeException(nameof(query), $"offset 必须在 0 到 {AiContextSchema.MaxQueryOffset} 之间。");
        ValidateDate(query.StartDate, "start_date");
        ValidateDate(query.EndDate, "end_date");
        if (query.StartDate is not null && query.EndDate is not null
            && string.CompareOrdinal(query.StartDate, query.EndDate) > 0)
            throw new ArgumentException("start_date 不能晚于 end_date。", nameof(query));
    }

    private static void EnsureDisclosed(bool disclosed, string section)
    {
        if (!disclosed)
            throw new InvalidOperationException($"当前快照未授权 {section} 数据节。");
    }

    private static void ValidateDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
            throw new ArgumentException($"{name} 必须使用 yyyy-MM-dd 格式。", name);
    }
}
