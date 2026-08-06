using Diary.Core.Configure;
using Diary.Core.Data.Base;
using Diary.Core.Utils;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Diary.App.Models;

public sealed class SavedWorkItemQueryTag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TagLevels Level { get; set; }
    public bool Unresolved { get; set; }
}

public sealed class SavedWorkItemQuery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public SavedWorkItemQueryTag[]? Tags { get; set; } = Array.Empty<SavedWorkItemQueryTag>();
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public int[]? TagIds { get; set; }
    public WorkItemTagFilter TagFilter { get; set; }
    public string? Text { get; set; }
    public WorkPriorities? Priority { get; set; }
    public int? Limit { get; set; }
    public int Offset { get; set; }

    public WorkItemQuery ToQuery() => WorkItemQueryNormalizer.Normalize(new WorkItemQuery
    {
        StartDate = StartDate,
        EndDate = EndDate,
        TagIds = Tags?.Select(tag => tag.Id).ToArray() ?? TagIds!,
        TagFilter = TagFilter,
        Text = Text,
        Priority = Priority,
        Limit = Limit,
        Offset = Offset,
    });

    public static SavedWorkItemQuery FromQuery(
        string name,
        WorkItemQuery query,
        IReadOnlyCollection<WorkTag>? availableTags = null)
    {
        query = WorkItemQueryNormalizer.Normalize(query);
        var tagsById = (availableTags ?? Array.Empty<WorkTag>()).ToDictionary(tag => tag.Id);
        return new SavedWorkItemQuery
        {
            Name = name.Trim(),
            StartDate = query.StartDate,
            EndDate = query.EndDate,
            Tags = query.TagIds.Select(id =>
            {
                if (!tagsById.TryGetValue(id, out var tag))
                    throw new ArgumentException($"找不到标签 {id}，无法保存标签快照", nameof(availableTags));
                return new SavedWorkItemQueryTag { Id = id, Name = tag.Name, Level = tag.Level };
            }).ToArray(),
            TagFilter = query.TagFilter,
            Text = query.Text,
            Priority = query.Priority,
            Limit = query.Limit,
            Offset = query.Offset,
        };
    }
}

[StorageFile("work_item_queries.json")]
public sealed class SavedWorkItemQueryStore
{
    public const int CurrentSchemaVersion = 2;

    private readonly bool _persistChanges;
    public int SchemaVersion { get; set; }
    public List<SavedWorkItemQuery> Queries { get; set; } = new();
    [JsonIgnore] public string LoadWarning { get; private set; } = string.Empty;

    public SavedWorkItemQueryStore(
        bool loadFromDisk = true,
        bool persistChanges = true,
        IReadOnlyCollection<WorkTag>? availableTags = null)
    {
        _persistChanges = persistChanges;
        if (!loadFromDisk)
        {
            SchemaVersion = CurrentSchemaVersion;
            return;
        }
        try
        {
            if (!EasySaveLoad.Load(this))
            {
                SchemaVersion = CurrentSchemaVersion;
                return;
            }
            var changed = NormalizeLoadedQueries(availableTags);
            if (changed && _persistChanges)
                EasySaveLoad.Save(this);
        }
        catch (Exception ex)
        {
            Logging.Logger.LogError(ex, "Failed to load saved work item queries");
            SchemaVersion = CurrentSchemaVersion;
            Queries = new List<SavedWorkItemQuery>();
            LoadWarning = "保存查询文件损坏，已忽略";
        }
    }

    public bool TryAdd(
        string name,
        WorkItemQuery query,
        out string error,
        IReadOnlyCollection<WorkTag>? availableTags = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ValidateName(name, null, out error))
            return false;
        try
        {
            var candidate = Queries.ToList();
            candidate.Add(SavedWorkItemQuery.FromQuery(name, query, availableTags));
            return TryCommit(candidate, out error);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryUpdate(
        Guid id,
        WorkItemQuery query,
        out string error,
        IReadOnlyCollection<WorkTag>? availableTags = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var candidate = Queries.ToList();
        var index = candidate.FindIndex(saved => saved.Id == id);
        if (index < 0)
        {
            error = "保存的查询不存在";
            return false;
        }
        try
        {
            var updated = SavedWorkItemQuery.FromQuery(candidate[index].Name, query, availableTags);
            updated.Id = id;
            candidate[index] = updated;
            return TryCommit(candidate, out error);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
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

    internal bool NormalizeLoadedQueries(IReadOnlyCollection<WorkTag>? availableTags = null)
    {
        if (SchemaVersion > CurrentSchemaVersion)
        {
            Queries = new List<SavedWorkItemQuery>();
            LoadWarning = $"保存查询格式版本 {SchemaVersion} 高于当前支持版本，未加载";
            return false;
        }
        var source = Queries ?? new List<SavedWorkItemQuery>();
        var changed = SchemaVersion != CurrentSchemaVersion;
        var legacy = SchemaVersion < CurrentSchemaVersion;
        var availableById = (availableTags ?? Array.Empty<WorkTag>())
            .GroupBy(tag => tag.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var normalized = new List<SavedWorkItemQuery>();
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repaired = 0;
        var dropped = 0;

        foreach (var saved in source)
        {
            if (saved is null || string.IsNullOrWhiteSpace(saved.Name))
            {
                dropped++;
                continue;
            }
            try
            {
                var name = UniqueName(saved.Name.Trim(), names);
                if (name != saved.Name)
                    repaired++;
                var id = saved.Id;
                if (id == Guid.Empty || !ids.Add(id))
                {
                    id = Guid.NewGuid();
                    ids.Add(id);
                    repaired++;
                }

                var tags = NormalizeTags(saved, legacy, availableById);
                var query = WorkItemQueryNormalizer.Normalize(new WorkItemQuery
                {
                    StartDate = saved.StartDate,
                    EndDate = saved.EndDate,
                    TagIds = tags.Select(tag => tag.Id).ToArray(),
                    TagFilter = saved.TagFilter,
                    Text = saved.Text,
                    Priority = saved.Priority,
                    Limit = saved.Limit,
                    Offset = saved.Offset,
                });
                normalized.Add(new SavedWorkItemQuery
                {
                    Id = id,
                    Name = name,
                    StartDate = query.StartDate,
                    EndDate = query.EndDate,
                    Tags = tags.Where(tag => query.TagIds.Contains(tag.Id)).ToArray(),
                    TagFilter = query.TagFilter,
                    Text = query.Text,
                    Priority = query.Priority,
                    Limit = query.Limit,
                    Offset = query.Offset,
                });
                changed |= saved.TagIds is not null
                    || saved.Tags is null
                    || name != saved.Name
                    || id != saved.Id
                    || saved.StartDate != query.StartDate
                    || saved.EndDate != query.EndDate
                    || saved.Text != query.Text
                    || saved.Tags?.Length != tags.Length
                    || saved.Tags?.Where(tag => tag is not null).Select(tag => tag.Id).SequenceEqual(
                        query.TagIds) == false;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                Logging.Logger.LogWarning(ex, "Ignoring invalid saved work item query {Name}", saved.Name);
                dropped++;
            }
        }

        changed |= dropped > 0 || repaired > 0 || normalized.Count != source.Count;
        Queries = normalized;
        SchemaVersion = CurrentSchemaVersion;
        if (dropped > 0 || repaired > 0 || legacy)
            LoadWarning = $"保存查询已迁移：修复 {repaired} 项，忽略 {dropped} 条坏记录";
        return changed;
    }

    private static SavedWorkItemQueryTag[] NormalizeTags(
        SavedWorkItemQuery saved,
        bool legacy,
        IReadOnlyDictionary<int, WorkTag> availableById)
    {
        if (legacy || saved.Tags is null)
        {
            if (!legacy && saved.TagIds is null)
                throw new ArgumentException("标签快照不能为空");
            var legacyIds = saved.TagIds ?? Array.Empty<int>();
            return legacyIds.Distinct().Select(id => availableById.TryGetValue(id, out var tag)
                ? new SavedWorkItemQueryTag { Id = id, Name = tag.Name, Level = tag.Level }
                : new SavedWorkItemQueryTag { Id = id, Unresolved = true }).ToArray();
        }

        var tags = new List<SavedWorkItemQueryTag>();
        foreach (var tag in saved.Tags)
        {
            if (tag is null || tag.Id <= 0 || !Enum.IsDefined(tag.Level)
                || string.IsNullOrWhiteSpace(tag.Name) && !tag.Unresolved)
                throw new ArgumentException("标签快照无效");
            if (tags.All(existing => existing.Id != tag.Id))
                tags.Add(new SavedWorkItemQueryTag
                {
                    Id = tag.Id,
                    Name = tag.Name.Trim(),
                    Level = tag.Level,
                    Unresolved = tag.Unresolved,
                });
        }
        return tags.ToArray();
    }

    private static string UniqueName(string name, ISet<string> names)
    {
        if (names.Add(name))
            return name;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (names.Add(candidate))
                return candidate;
        }
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
                var package = new SavedWorkItemQueryStore(false)
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Queries = candidate,
                };
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
        Tags = saved.Tags?.Select(tag => new SavedWorkItemQueryTag
        {
            Id = tag.Id,
            Name = tag.Name,
            Level = tag.Level,
            Unresolved = tag.Unresolved,
        }).ToArray(),
        TagFilter = saved.TagFilter,
        Text = saved.Text,
        Priority = saved.Priority,
        Limit = saved.Limit,
        Offset = saved.Offset,
    };
}
