using Diary.Core.Data.Base;
using Diary.Database;
using Diary.Survey;
using Diary.Utils;

namespace Diary.App.Models;

public static class SurveyStatisticsBuilder
{
    public static bool TryBuildQuery(
        DbInterfaceBase db,
        ExtendedSurveyRequest request,
        out WorkItemQuery query,
        out string error)
    {
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

    public static RespondData Build(DbInterfaceBase db, WorkItemQuery query)
    {
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
        return data;
    }
}
