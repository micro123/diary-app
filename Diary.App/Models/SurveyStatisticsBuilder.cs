using Diary.Core.Data.Base;
using Diary.Database;
using Diary.Survey;
using Diary.Utils;

namespace Diary.App.Models;

public static class SurveyStatisticsBuilder
{
    public const int MaxDetails = 500;

    public static IReadOnlyList<string> SupportedGroupDimensions { get; } =
    [
        ExtendedSurveyProtocol.GroupByTag,
        ExtendedSurveyProtocol.GroupByDate,
        ExtendedSurveyProtocol.GroupByPriority,
    ];

    public static string NormalizeGroupBy(string? groupBy)
        => string.IsNullOrWhiteSpace(groupBy)
            ? ExtendedSurveyProtocol.GroupByTag
            : groupBy.Trim().ToLowerInvariant();

    public static bool TryBuildQuery(
        DbInterfaceBase db,
        ExtendedSurveyRequest request,
        out WorkItemQuery query,
        out string error)
    {
        var groupBy = NormalizeGroupBy(request.GroupBy);
        if (!SupportedGroupDimensions.Contains(groupBy, StringComparer.Ordinal))
        {
            query = new WorkItemQuery();
            error = "分组维度无效";
            return false;
        }

        if (!Enum.TryParse<WorkItemTagFilter>(request.TagFilter, true, out var tagFilter))
        {
            query = new WorkItemQuery();
            error = "标签筛选模式无效";
            return false;
        }

        WorkPriorities? priority = null;
        if (request.Priority is not null)
        {
            if (!Enum.IsDefined(typeof(WorkPriorities), request.Priority.Value))
            {
                query = new WorkItemQuery();
                error = "优先级无效";
                return false;
            }
            priority = (WorkPriorities)request.Priority.Value;
        }

        var requestedNames = request.TagNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagIds = db.AllWorkTags()
            .Where(tag => requestedNames.Contains(tag.Name))
            .Select(tag => tag.Id)
            .ToArray();

        var candidate = new WorkItemQuery
        {
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Text = request.Text,
            TagIds = tagIds,
            TagFilter = tagFilter,
            Priority = priority,
        };
        return WorkItemQueryNormalizer.TryNormalize(candidate, out query, out error);
    }

    public static RespondData Build(
        DbInterfaceBase db,
        WorkItemQuery query,
        string groupBy,
        bool includeDetails)
    {
        groupBy = NormalizeGroupBy(groupBy);
        var items = db.QueryWorkItems(query).ToArray();
        var tagsByItem = db.GetWorkTagsByWorkItemIds(items.Select(item => item.Id).ToArray());
        var data = new RespondData
        {
            Hostname = SysInfo.GetHostname(),
            Username = SysInfo.GetUsername(),
            DateStart = query.StartDate ?? string.Empty,
            DateEnd = query.EndDate ?? string.Empty,
            TotalTime = items.Sum(item => item.Time),
            RecordCount = items.Length,
            GroupBy = groupBy,
        };

        var primaryMap = new Dictionary<int, RespondTag>();
        var nestedMap = new Dictionary<(int PrimaryId, int NestedId), RespondTag>();
        foreach (var item in items)
        {
            if (!tagsByItem.TryGetValue(item.Id, out var tags))
                continue;

            var primaryTags = tags.Where(tag => tag.Level == TagLevels.Primary).ToArray();
            foreach (var primary in primaryTags)
            {
                if (!primaryMap.TryGetValue(primary.Id, out var primaryData))
                {
                    primaryData = new RespondTag { TagName = primary.Name };
                    primaryMap.Add(primary.Id, primaryData);
                    data.Tags.Add(primaryData);
                }
                primaryData.TagTime += item.Time;

                foreach (var nested in tags.Where(tag => tag.Level == TagLevels.Secondary))
                {
                    var key = (primary.Id, nested.Id);
                    if (!nestedMap.TryGetValue(key, out var nestedData))
                    {
                        nestedData = new RespondTag { TagName = nested.Name };
                        nestedMap.Add(key, nestedData);
                        primaryData.SubTags.Add(nestedData);
                    }
                    nestedData.TagTime += item.Time;
                }
            }
        }

        var taggedTotal = data.Tags.Sum(tag => tag.TagTime);
        if (taggedTotal < data.TotalTime)
            data.Tags.Add(new RespondTag { TagTime = data.TotalTime - taggedTotal });

        data.Groups = BuildGroups(items, tagsByItem, groupBy);
        if (includeDetails)
        {
            data.Details = items
                .Take(MaxDetails)
                .Select(item => new RespondDetail
                {
                    Date = item.CreateDate,
                    Comment = item.Comment,
                    Time = item.Time,
                    Priority = item.Priority.ToString(),
                    Tags = tagsByItem.TryGetValue(item.Id, out var tags)
                        ? tags.Select(tag => tag.Name).Distinct(StringComparer.Ordinal).ToArray()
                        : Array.Empty<string>(),
                })
                .ToList();
            data.DetailsTruncated = items.Length > MaxDetails;
        }

        return data;
    }

    private static List<RespondGroup> BuildGroups(
        IReadOnlyCollection<WorkItem> items,
        IReadOnlyDictionary<int, ICollection<WorkTag>> tagsByItem,
        string groupBy)
    {
        var groups = new Dictionary<string, RespondGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var names = GetGroupNames(item, tagsByItem, groupBy);
            foreach (var name in names)
            {
                if (!groups.TryGetValue(name, out var group))
                {
                    group = new RespondGroup { Name = name };
                    groups.Add(name, group);
                }

                group.TotalTime += item.Time;
                group.RecordCount++;
            }
        }

        return groups.Values
            .OrderByDescending(group => group.TotalTime)
            .ThenBy(group => group.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> GetGroupNames(
        WorkItem item,
        IReadOnlyDictionary<int, ICollection<WorkTag>> tagsByItem,
        string groupBy)
    {
        if (groupBy == ExtendedSurveyProtocol.GroupByDate)
            return [item.CreateDate.Split('T', 2)[0]];

        if (groupBy == ExtendedSurveyProtocol.GroupByPriority)
            return [item.Priority.ToString()];

        if (!tagsByItem.TryGetValue(item.Id, out var tags))
            return [RespondTag.AnonymousName];

        var names = tags
            .Where(tag => tag.Level == TagLevels.Primary)
            .Select(tag => tag.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return names.Length == 0 ? [RespondTag.AnonymousName] : names;
    }
}
