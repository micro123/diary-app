using Diary.Core.Configure;
using Diary.Core.Data.Base;
using Diary.Core.Utils;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Models;

public sealed class SavedWorkItemQuery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public int[] TagIds { get; set; } = Array.Empty<int>();
    public WorkItemTagFilter TagFilter { get; set; }
    public string? Text { get; set; }
    public WorkPriorities? Priority { get; set; }

    public WorkItemQuery ToQuery() => new()
    {
        StartDate = StartDate,
        EndDate = EndDate,
        TagIds = TagIds,
        TagFilter = TagFilter,
        Text = Text,
        Priority = Priority,
    };

    public static SavedWorkItemQuery FromQuery(string name, WorkItemQuery query) => new()
    {
        Name = name.Trim(),
        StartDate = query.StartDate,
        EndDate = query.EndDate,
        TagIds = query.TagIds.Distinct().ToArray(),
        TagFilter = query.TagFilter,
        Text = query.Text,
        Priority = query.Priority,
    };
}

[StorageFile("work_item_queries.json")]
public sealed class SavedWorkItemQueryStore
{
    private readonly bool _persistChanges;
    public List<SavedWorkItemQuery> Queries { get; set; } = new();

    public SavedWorkItemQueryStore(bool loadFromDisk = true, bool persistChanges = true)
    {
        _persistChanges = persistChanges;
        if (!loadFromDisk)
            return;
        try
        {
            EasySaveLoad.Load(this);
            Queries ??= new List<SavedWorkItemQuery>();
        }
        catch (Exception ex)
        {
            Logging.Logger.LogError(ex, "Failed to load saved work item queries");
            Queries = new List<SavedWorkItemQuery>();
        }
    }

    public bool TryAdd(string name, WorkItemQuery query, out string error)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ValidateName(name, null, out error))
            return false;
        var candidate = Queries.ToList();
        candidate.Add(SavedWorkItemQuery.FromQuery(name, query));
        return TryCommit(candidate, out error);
    }

    public bool TryUpdate(Guid id, WorkItemQuery query, out string error)
    {
        ArgumentNullException.ThrowIfNull(query);
        var candidate = Queries.ToList();
        var index = candidate.FindIndex(saved => saved.Id == id);
        if (index < 0)
        {
            error = "保存的查询不存在";
            return false;
        }
        var updated = SavedWorkItemQuery.FromQuery(candidate[index].Name, query);
        updated.Id = id;
        candidate[index] = updated;
        return TryCommit(candidate, out error);
    }

    public bool TryRename(Guid id, string name, out string error)
    {
        if (!ValidateName(name, id, out error))
            return false;
        var candidate = Queries.Select(Clone).ToList();
        var saved = candidate.FirstOrDefault(item => item.Id == id);
        if (saved is null)
        {
            error = "保存的查询不存在";
            return false;
        }
        saved.Name = name.Trim();
        return TryCommit(candidate, out error);
    }

    public bool TryDelete(Guid id, out string error)
    {
        var candidate = Queries.Where(saved => saved.Id != id).ToList();
        if (candidate.Count == Queries.Count)
        {
            error = "保存的查询不存在";
            return false;
        }
        return TryCommit(candidate, out error);
    }

    private bool ValidateName(string name, Guid? exceptId, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "请输入查询名称";
            return false;
        }
        if (Queries.Any(saved => saved.Id != exceptId
            && string.Equals(saved.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            error = "查询名称已存在";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private bool TryCommit(List<SavedWorkItemQuery> candidate, out string error)
    {
        try
        {
            if (_persistChanges)
            {
                var package = new SavedWorkItemQueryStore(false) { Queries = candidate };
                if (!EasySaveLoad.Save(package))
                {
                    error = "保存查询失败";
                    return false;
                }
            }
            Queries = candidate;
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            Logging.Logger.LogError(ex, "Failed to persist saved work item queries");
            error = "保存查询失败，请稍后重试";
            return false;
        }
    }

    private static SavedWorkItemQuery Clone(SavedWorkItemQuery saved) => new()
    {
        Id = saved.Id,
        Name = saved.Name,
        StartDate = saved.StartDate,
        EndDate = saved.EndDate,
        TagIds = saved.TagIds.ToArray(),
        TagFilter = saved.TagFilter,
        Text = saved.Text,
        Priority = saved.Priority,
    };
}
