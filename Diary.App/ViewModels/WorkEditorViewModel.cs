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

    // db data fields
    private WorkItem? WorkItem { get; set; } // ref to existed db item, may null

    // tracker 区（RedMine 等）。无 tracker 时为 null，编辑器只渲染 generic 字段。
    private ITrackerEditorExtension? _tracker;
    public ViewModelBase? TrackerRegion { get; private set; }

    // generic data
    [ObservableProperty] private string _date;
    [ObservableProperty] private string _comment;
    [ObservableProperty] private string _note;
    [ObservableProperty] private double _time;
    [ObservableProperty] private WorkPriorities _priority;
    [ObservableProperty] private ObservableCollection<WorkTag> _workTags = new();
    [ObservableProperty] private ObservableCollection<WorkTag> _availableTags = new();

    /// <summary>是否锁住 generic 编辑字段（=tracker 区已上传到远程）。</summary>
    [ObservableProperty] private bool _isLocked;

    public ObservableCollection<WorkTag> AllTags => _shareData.WorkTags;

    // todo: plm?

    private DbInterfaceBase? Db => App.Instance.UseDb;

    public static WorkEditorViewModel FromWorkItem(WorkItem workItem)
    {
        return new WorkEditorViewModel(App.Instance.Services.GetRequiredService<DbShareData>())
        {
            WorkId = workItem.Id,
            WorkItem = workItem,
            Date = workItem.CreateDate,
            Comment = workItem.Comment,
            Time = workItem.Time,
            Priority = workItem.Priority,
        };
    }

    public WorkEditorViewModel(DbShareData shareData)
    {
        _shareData = shareData;
        Date = TimeTools.Today();
        Comment = App.Instance.AppConfig.WorkSettings.DefaultTaskTitle;
        Note = string.Empty;
        Time = 0.0;
        Priority = WorkPriorities.P0;

        // 解析当前 tracker（首个注册的；M2 仅 RedMine），创建编辑器区。
        var tracker = App.Instance.Services.GetService<IEnumerable<ITrackerUiContribution>>()?.FirstOrDefault();
        _tracker = tracker?.CreateEditorExtension(tracker?.Instance.InstanceId ?? string.Empty);
        TrackerRegion = _tracker as ViewModelBase;

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
        created = false;
        var db = Db!;
        if (WorkItem == null)
        {
            WorkItem = db.CreateWorkItem(Date, Comment);
            if (WorkItem.Id <= 0)
            {
                EventDispatcher.ShowToast("保存失败了！");
                return;
            }

            WorkId = WorkItem.Id;
            WorkItem.Priority = Priority;
            WorkItem.Time = Time;
            created = true;
        }
        else
        {
            WorkItem.CreateDate = Date;
            WorkItem.Comment = Comment;
            WorkItem.Time = Time;
            WorkItem.Priority = Priority;
        }

        // 一般信息
        db.UpdateWorkItem(WorkItem);

        // 笔记
        if (!string.IsNullOrWhiteSpace(Note))
        {
            db.WorkUpdateNote(WorkItem, Note);
        }
        else
        {
            db.WorkDeleteNote(WorkItem);
        }

        // tracker 绑定（如 RedMine 的 issue/activity → CreateWorkTimeEntry）
        _tracker?.Save(WorkItem);

        // 首次创建则全部添加标签
        if (created)
        {
            foreach (var workTag in WorkTags)
            {
                Db!.WorkItemAddTag(WorkItem, workTag);
            }
        }
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
        return _tracker?.CanDelete ?? true;
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
        _tracker?.Load(WorkItem);
        IsLocked = _tracker?.IsLocked ?? false;
    }

    public void SyncFromBatch(
        Dictionary<int, string> notesById,
        Dictionary<int, ICollection<WorkTag>> tagsById,
        IDictionary<int, object?>? bindingsById)
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

        var binding = bindingsById != null && bindingsById.TryGetValue(id, out var b)
            ? b
            : null;
        _tracker?.Load(WorkItem, binding);
        IsLocked = _tracker?.IsLocked ?? false;
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
        // tracker 区选择复制到新 editor 的 region
        _tracker?.CloneTo(result._tracker);

        return result;
    }

    public bool CanClone()
    {
        return WorkItem is { Id: > 0 }; // 克隆的前提是这个事件已经保存过了
    }

    [RelayCommand]
    private void AddTag(WorkTag tag)
    {
        if (WorkTags.Contains(tag))
            return;
        _syncing_tags = true;
        if (WorkItem is { Id: > 0 })
            Db!.WorkItemAddTag(WorkItem, tag);
        WorkTags.Add(tag);
        _syncing_tags = false;
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

    public async Task<(bool, string?)> Upload()
    {
        if (_tracker is null)
            return (false, "无可用 tracker");
        var r = await _tracker.UploadAsync(WorkItem!);
        IsLocked = _tracker.IsLocked;
        return (r.Success, r.Error);
    }

    /// <summary>模板默认值应用：按 id 选中 activity（RedMine 语义）。</summary>
    public void SetRedMineActivity(int activityId) => _tracker?.SetActivity(activityId);

    /// <summary>模板默认值应用：按 id 选中 issue（RedMine 语义）。</summary>
    public void SetRedMineIssues(int issueId) => _tracker?.SetIssue(issueId);
}
