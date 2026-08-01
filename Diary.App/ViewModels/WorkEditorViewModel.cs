using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.App.Utils;
using Diary.Core.Data.Base;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;
using Diary.Database;
using Diary.GUIBase.Utils;
using Diary.Utils;
using Diary.GUIBase.ViewModels;
using Diary.RedMine;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.App.ViewModels;

public partial class WorkEditorViewModel : ViewModelBase
{
    private readonly DbShareData _shareData;

    // db data fields
    private WorkItem? WorkItem { get; set; } // ref to existed db item, may null
    private WorkTimeEntry? TimeEntry { get; set; }

    // generic data
    [ObservableProperty] private string _date;
    [ObservableProperty] private string _comment;
    [ObservableProperty] private string _note;
    [ObservableProperty] private double _time;
    [ObservableProperty] private WorkPriorities _priority;
    [ObservableProperty] private ObservableCollection<WorkTag> _workTags = new();
    [ObservableProperty] private ObservableCollection<WorkTag> _availableTags = new();

    public ObservableCollection<WorkTag> AllTags => _shareData.WorkTags;

    // todo: redmine date
    public ObservableCollection<RedMineIssueDisplay> RedMineIssues => _shareData.RedMineIssuesOpen;
    public ObservableCollection<RedMineActivity> RedMineActivities => _shareData.RedMineActivities;
    [ObservableProperty] private int _issueIndex = -1;
    [ObservableProperty] private int _activityIndex = -1;
    [ObservableProperty] private bool _uploaded = false;
    [ObservableProperty] private string _issueText = string.Empty;

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

        // 保存redmine信息，如果有效的话
        if (IssueIndex >= 0 && ActivityIndex >= 0)
        {
            TimeEntry = Db!.CreateWorkTimeEntry(WorkItem.Id, RedMineActivities[ActivityIndex].Id,
                RedMineIssues[IssueIndex].Id);
        }

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
        return !Uploaded;
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
        SyncRedMine();
    }

    public void SyncFromBatch(
        Dictionary<int, string> notesById,
        Dictionary<int, ICollection<WorkTag>> tagsById,
        Dictionary<int, WorkTimeEntry> timeEntriesById)
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

        if (timeEntriesById.TryGetValue(id, out var timeEntry))
        {
            TimeEntry = timeEntry;
            SyncRedMineFromEntry();
        }
    }

    private void SyncRedMineFromEntry()
    {
        if (TimeEntry == null)
        {
            IssueIndex = ActivityIndex = -1;
            IssueText = string.Empty;
            return;
        }

        for (var i = 0; i < RedMineIssues.Count; i++)
        {
            if (TimeEntry.IssueId == RedMineIssues[i].Id)
            {
                IssueIndex = i;
                IssueText = $"#{RedMineIssues[i].Id} {RedMineIssues[i].Title} ({RedMineIssues[i].Project})";
                break;
            }
        }

        for (var i = 0; i < RedMineActivities.Count; i++)
        {
            if (TimeEntry.ActivityId == RedMineActivities[i].Id)
            {
                ActivityIndex = i;
                break;
            }
        }

        Uploaded = TimeEntry.EntryId > 0;
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

    public void SyncRedMine()
    {
        TimeEntry = null;
        if (WorkItem is { Id: > 0 })
        {
            TimeEntry = Db!.WorkItemGetTimeEntry(WorkItem);
        }

        SyncRedMineFromEntry();
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
            IssueIndex = IssueIndex,
            IssueText = IssueText,
            ActivityIndex = ActivityIndex,
        };
        foreach (var tag in WorkTags)
        {
            result.WorkTags.Add(tag);
        }

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

    private bool CanUpload()
    {
        return IssueIndex >= 0 && ActivityIndex >= 0 && Time > 0; // new item and both set
    }

    public async Task<(bool, string?)> Upload()
    {
        if (Uploaded)
            return (false, null);
        if (!CanUpload())
            return (false, "问题或活动不正确，又或者耗时是0");
        Debug.Assert(WorkItem is not null);
        Debug.Assert(TimeEntry is not null);

        // 网络 API 调用与 DB 写入一并放到后台线程，避免在 UI 线程同步写库造成卡顿
        var entryId = 0;
        await Task.Run(() =>
        {
            if (RedMineApis.CreateTimeEntry(out var ti, TimeEntry.IssueId,
                    TimeEntry.ActivityId, WorkItem.CreateDate,
                    WorkItem.Time, WorkItem.Comment))
                entryId = ti.Id;
            TimeEntry.EntryId = entryId;
            Db!.UpdateWorkTimeEntry(TimeEntry); // 关联到数据库
        });
        // 绑定属性必须在 UI 线程更新（await 恢复点）
        Uploaded = entryId > 0;
        return (Uploaded, Uploaded ? null : "可能是网络问题");
    }

    public void SetRedMineActivity(int activityId)
    {
        for (var i = 0; i < RedMineActivities.Count; i++)
        {
            if (RedMineActivities[i].Id == activityId)
            {
                ActivityIndex = i;
                return;
            }
        }

        ActivityIndex = -1;
    }

    public void SetRedMineIssues(int issueId)
    {
        for (var i = 0; i < RedMineIssues.Count; i++)
        {
            var x =  RedMineIssues[i];
            if (x.Id == issueId)
            {
                IssueIndex = i;
                IssueText = $"#{x.Id} {x.Title} ({x.Project})";
                return;
            }
        }

        IssueIndex = -1;
        IssueText = string.Empty;
    }
}