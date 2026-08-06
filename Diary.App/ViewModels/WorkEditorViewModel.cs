using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.App.ViewModels;

public partial class WorkEditorViewModel : ViewModelBase
{
    private readonly DbShareData _shareData;
    private readonly IWorkItemPersistenceCoordinator _persistence;
    private readonly ITrackerUploadCoordinator _uploadCoordinator;
    private readonly ITagAutomationCoordinator _tagAutomation;

    // db data fields
    private WorkItem? WorkItem { get; set; } // ref to existed db item, may null

    // tracker 扩展集合（RedMine 等，可多个）。无 tracker 时空集合，编辑器只渲染 generic 字段。
    public ObservableCollection<ITrackerEditorExtension> Extensions { get; } = new();
    public ObservableCollection<TrackerUploadResult> UploadResults { get; } = new();
    [ObservableProperty] private TagAutomationResult? _lastTagAutomationResult;

    // generic data
    [ObservableProperty] private string _date;
    [ObservableProperty] private string _comment;
    [ObservableProperty] private string _note;
    [ObservableProperty] private double _time;
    [ObservableProperty] private WorkPriorities _priority;
    [ObservableProperty] private ObservableCollection<WorkTag> _workTags = new();
    [ObservableProperty] private ObservableCollection<WorkTag> _availableTags = new();

    /// <summary>是否锁住 generic 编辑字段（任一 tracker 区已上传到远程即锁定）。</summary>
    [ObservableProperty] private bool _isLocked;

    public ObservableCollection<WorkTag> AllTags => _shareData.WorkTags;

    // todo: plm?

    private DbInterfaceBase? Db => App.Instance.UseDb;

    public static WorkEditorViewModel FromWorkItem(WorkItem workItem)
    {
        return new WorkEditorViewModel(
            App.Instance.Services.GetRequiredService<DbShareData>(),
            App.Instance.Services.GetRequiredService<IWorkItemPersistenceCoordinator>(),
            App.Instance.Services.GetRequiredService<ITrackerUploadCoordinator>())
        {
            WorkId = workItem.Id,
            WorkItem = workItem,
            Date = workItem.CreateDate,
            Comment = workItem.Comment,
            Time = workItem.Time,
            Priority = workItem.Priority,
        };
    }

    public WorkEditorViewModel(
        DbShareData shareData,
        IWorkItemPersistenceCoordinator? persistence = null,
        ITrackerUploadCoordinator? uploadCoordinator = null,
        TrackerUiContributionRegistry? trackerRegistry = null,
        string? defaultTaskTitle = null,
        ITagAutomationCoordinator? tagAutomation = null)
    {
        _shareData = shareData;
        _persistence = persistence
            ?? App.Instance.Services.GetRequiredService<IWorkItemPersistenceCoordinator>();
        _uploadCoordinator = uploadCoordinator
            ?? App.Instance.Services.GetRequiredService<ITrackerUploadCoordinator>();
        _tagAutomation = tagAutomation
            ?? App.Instance.Services.GetRequiredService<ITagAutomationCoordinator>();
        Date = TimeTools.Today();
        Comment = defaultTaskTitle ?? App.Instance.AppConfig.WorkSettings.DefaultTaskTitle;
        Note = string.Empty;
        Time = 0.0;
        Priority = WorkPriorities.P0;

        // 解析全部已注册 tracker，为每个创建一个编辑器扩展。
        var trackers = (trackerRegistry ?? App.Instance.Services
            .GetRequiredService<TrackerUiContributionRegistry>()).Contributions;
        foreach (var t in trackers)
        {
            try
            {
                var ext = t.CreateEditorExtension(t.Instance.InstanceId);
                if (ext is not null && Extensions.All(existing => existing.Key != ext.Key))
                    Extensions.Add(ext);
            }
            catch (Exception ex)
            {
                LastTagAutomationResult = new TagAutomationResult([
                    new TagAutomationInstanceResult(
                        new TrackerKey(t.PluginId, t.Instance.InstanceId),
                        false,
                        Array.Empty<string>(),
                        Array.Empty<TrackerTagDefaultConflict>(),
                        Array.Empty<TrackerTagDefaultInvalidTarget>(),
                        $"创建编辑扩展失败: {ex.Message}")
                ]);
            }
        }

        WorkTags.CollectionChanged += (_, _) =>
        {
            if (_syncing_tags)
                return;
            UpdateAvailableTags();
        };
    }

    public bool IsDateChanged => WorkItem is not null && WorkItem.CreateDate != Date;

    public bool IsNewItem => WorkItem is null;

    // public int WorkId => WorkItem?.Id ?? 0;
    [ObservableProperty] private int _workId;

    public void Save(out bool created)
    {
        var db = Db!;
        var result = _persistence.Save(db, new WorkItemSaveRequest(
            WorkItem, Date, Comment, Note, Time, Priority, WorkTags, Extensions));
        created = result.Created;
        if (!result.Success || result.WorkItem is null)
        {
            EventDispatcher.ShowToast(result.Error ?? "保存失败了！");
            return;
        }

        WorkItem = result.WorkItem;
        WorkId = WorkItem.Id;
    }

    public void Delete()
    {
        // remove from db
        if (WorkItem is { Id: > 0 })
            Db!.DeleteWorkItem(WorkItem!);
        WorkItem = null;
    }

    public bool CanDelete()
    {
        return Extensions.Count == 0 || Extensions.All(e => e.CanDelete);
    }

    [RelayCommand]
    private void QuickDate(string what)
    {
        Date = what switch
        {
            "0" => TimeTools.Today(),
            "+1" => TimeTools.Tomorrow(),
            "-1" => TimeTools.Yestoday(),
            _ => Date
        };
    }

    public void SyncAll()
    {
        SyncNote();
        SyncTags();
        foreach (var ext in Extensions)
            ext.Load(WorkItem);
        RecomputeIsLocked();
    }

    public void SyncFromBatch(
        Dictionary<int, string> notesById,
        Dictionary<int, ICollection<WorkTag>> tagsById,
        IReadOnlyDictionary<TrackerKey, IDictionary<int, object?>?>? bindingsByTracker)
    {
        if (WorkItem is not { Id: > 0 })
            return;

        var id = WorkItem.Id;

        if (notesById.TryGetValue(id, out var note))
            Note = note;
        else
            Note = string.Empty;

        // 无论该工作项是否已有标签，都要重建 WorkTags 并刷新可选标签列表，
        // 否则无标签项的 AvailableTags 永远是空的（添加标签列表列不出任何标签）。
        _syncing_tags = true;
        WorkTags.Clear();
        if (tagsById.TryGetValue(id, out var tags))
        {
            foreach (var tag in tags)
                WorkTags.Add(tag);
        }
        _syncing_tags = false;
        UpdateAvailableTags();

        // 每个 tracker 扩展从 map 中按 InstanceId 取自己的 per-work 绑定
        foreach (var ext in Extensions)
        {
            object? binding = null;
            if (bindingsByTracker != null
                && bindingsByTracker.TryGetValue(ext.Key, out var perTracker)
                && perTracker != null
                && perTracker.TryGetValue(id, out var bv))
            {
                binding = bv;
            }
            ext.Load(WorkItem, binding);
        }
        RecomputeIsLocked();
    }

    private void SyncNote()
    {
        if (WorkItem is { Id: > 0 })
        {
            Note = Db!.WorkGetNote(WorkItem!) ?? string.Empty;
        }
    }

    private bool _syncing_tags;

    public void SyncTags()
    {
        _syncing_tags = true;
        if (WorkItem is { Id: > 0 })
        {
            WorkTags.Clear();
            var tags = Db!.GetWorkItemTags(WorkItem);
            foreach (var tag in tags)
            {
                WorkTags.Add(tag);
            }
        }

        UpdateAvailableTags();
        _syncing_tags = false;
    }

    public WorkEditorViewModel Clone()
    {
        var result = new WorkEditorViewModel(_shareData)
        {
            WorkItem = null,
            Date = Date,
            Note = Note,
            Comment = Comment,
            Time = 0.0,
            Priority = Priority,
        };
        foreach (var tag in WorkTags)
        {
            result.WorkTags.Add(tag);
        }
        var targetExtensions = result.Extensions.ToDictionary(extension => extension.Key);
        foreach (var extension in Extensions)
        {
            if (targetExtensions.TryGetValue(extension.Key, out var target))
                extension.CloneTo(target);
        }

        return result;
    }

    public bool CanClone()
    {
        return WorkItem is { Id: > 0 }; // 克隆的前提是这个事件已经保存过了
    }

    public bool CanUpload()
        => WorkItem is { Id: > 0 } && Extensions.Any(extension => !extension.IsLocked);

    [RelayCommand]
    private void AddTag(WorkTag tag)
        => AddTags([tag], TagAddSource.User);

    public void AddTags(IEnumerable<WorkTag> tags, TagAddSource source)
    {
        var sequence = 0;
        foreach (var tag in tags)
        {
            if (WorkTags.Any(existing => existing.Id == tag.Id))
                continue;
            _syncing_tags = true;
            if (WorkItem is { Id: > 0 })
            {
                if (Db is null || !Db.WorkItemAddTag(WorkItem, tag))
                {
                    _syncing_tags = false;
                    LastTagAutomationResult = new TagAutomationResult([
                        new TagAutomationInstanceResult(
                            new TrackerKey("core", "local"),
                            false,
                            Array.Empty<string>(),
                            Array.Empty<TrackerTagDefaultConflict>(),
                            Array.Empty<TrackerTagDefaultInvalidTarget>(),
                            "标签保存失败")
                    ]);
                    continue;
                }
            }
            WorkTags.Add(tag);
            _syncing_tags = false;
            LastTagAutomationResult = _tagAutomation.TagAdded(
                WorkItem,
                tag,
                new TagAutomationContext(source, sequence++),
                Extensions);
        }
        UpdateAvailableTags();
    }

    [RelayCommand]
    private void DelTag(WorkTag tag)
    {
        _syncing_tags = true;
        if (WorkItem is { Id: > 0 })
        {
            if (tag.Level == TagLevels.Primary)
            {
                Db!.WorkItemCleanTags(WorkItem);
                WorkTags.Clear();
            }
            else
            {
                Db!.WorkItemRemoveTag(WorkItem, tag);
                WorkTags.Remove(tag);
            }
        }
        else
        {
            if (tag.Level == TagLevels.Primary)
                WorkTags.Clear();
            else
                WorkTags.Remove(tag);
        }
        _syncing_tags = false;
        UpdateAvailableTags();
    }

    private void UpdateAvailableTags()
    {
        AvailableTags.Clear();
        if (WorkTags.Count > 0)
        {
            // show only secondary tags
            foreach (var tag in AllTags.Where(x => x is { Level: TagLevels.Secondary, Disabled: false }))
            {
                if (!WorkTags.Contains(tag))
                    AvailableTags.Add(tag);
            }
        }
        else
        {
            // show only primary tags
            foreach (var tag in AllTags.Where(x => x is { Level: TagLevels.Primary, Disabled: false }))
            {
                AvailableTags.Add(tag);
            }
        }
    }

    /// <summary>上传所有 tracker 扩展，聚合结果。任一失败即整体失败。</summary>
    public async Task<(bool, string?)> Upload()
    {
        if (WorkItem is null)
            return (false, "工作项尚未保存");
        var result = await _uploadCoordinator.UploadAsync(WorkItem, Extensions);
        UploadResults.Clear();
        foreach (var uploadResult in result.Results)
            UploadResults.Add(uploadResult);
        RecomputeIsLocked();
        return (result.Success, result.Error);
    }

    private void RecomputeIsLocked() => IsLocked = Extensions.Any(e => e.IsLocked);
}
