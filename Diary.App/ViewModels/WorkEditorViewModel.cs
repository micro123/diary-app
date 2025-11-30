using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.App.Utils;
using Diary.Core.Data.Base;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;
using Diary.Database;
using Diary.RedMine;
using Diary.Utils;
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
    public ObservableCollection<RedMineIssueDisplay> RedMineIssues => _shareData.RedMineIssues;
    public ObservableCollection<RedMineActivity> RedMineActivities => _shareData.RedMineActivities;
    [ObservableProperty] private int _issueIndex = -1;
    [ObservableProperty] private int _activityIndex = -1;
    [ObservableProperty] private bool _uploaded = false;

    // todo: plm?

    private DbInterfaceBase? Db => App.Current.UseDb;

    public static WorkEditorViewModel FromWorkItem(WorkItem workItem)
    {
        return new WorkEditorViewModel(App.Current.Services.GetRequiredService<DbShareData>())
        {
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
        Comment = App.Current.AppConfig.WorkSettings.DefaultTaskTitle;
        Note = string.Empty;
        Time = 0.0;
        Priority = WorkPriorities.P0;

        WorkTags.CollectionChanged += (sender, args) =>
        {
            if (_syncing_tags)
                return;
            var tag = args.Action switch
            {
                NotifyCollectionChangedAction.Add => (WorkTag?)args.NewItems![0],
                NotifyCollectionChangedAction.Remove => (WorkTag?)args.OldItems![0],
                _ => null
            };
            if (WorkItem is { Id: > 0 })
            {
                // 这里处理添加删除标签
                switch (args.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        Db!.WorkItemAddTag(WorkItem, tag!);
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        if (tag!.Level == TagLevels.Primary)
                        {
                            // 在主线程全部移除标签
                            Dispatcher.UIThread.Post(() =>
                            {
                                _syncing_tags = true;
                                while(WorkTags.Count > 0)
                                    WorkTags.RemoveAt(0);
                                _syncing_tags = false;
                            });
                        }
                        Db!.WorkItemRemoveTag(WorkItem, tag!);
                        break;
                }
            }
            else if (tag is { Level:  TagLevels.Primary } && args.Action == NotifyCollectionChangedAction.Remove)
            {
                // 在主线程全部移除标签
                Dispatcher.UIThread.Post(() =>
                {
                    _syncing_tags = true;
                    while(WorkTags.Count > 0)
                        WorkTags.RemoveAt(0);
                    _syncing_tags = false;
                });
            }
            UpdateAvailableTags();
        };
    }

    public bool IsDateChanged => WorkItem is not null && WorkItem.CreateDate != Date;
    public bool IsNewItem => WorkItem is null;
    public int WorkId => WorkItem?.Id ?? 0;

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
            TimeEntry = Db!.CreateWorkTimeEntry(WorkItem.Id, RedMineActivities[ActivityIndex].Id, RedMineIssues[IssueIndex].Id);
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
        Db!.DeleteWorkItem(WorkItem!);
        WorkItem = null;
    }

    public bool CanDelete()
    {
        return WorkItem != null && WorkItem.Id != 0;
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
        
        if (TimeEntry != null)
        {
            var i = 0;
            while (i < RedMineIssues.Count)
            {
                if (TimeEntry.IssueId == RedMineIssues[i].Id)
                {
                    IssueIndex = i;
                    break;
                }

                ++i;
            }

            i = 0;
            while (i < RedMineActivities.Count)
            {
                if (TimeEntry.ActivityId == RedMineActivities[i].Id)
                {
                    ActivityIndex = i;
                    break;
                }

                ++i;
            }

            Uploaded = TimeEntry.EntryId > 0;
        }
        else
        {
            IssueIndex = ActivityIndex = -1;
        }
    }

    public WorkEditorViewModel Clone()
    {
        return new WorkEditorViewModel(_shareData)
        {
            WorkItem = null,
            Date = Date,
            Note = Note,
            Comment = Comment,
            Priority = Priority,
        };
    }

    public bool CanClone()
    {
        return WorkItem is { Id: > 0 }; // 克隆的前提是这个事件已经保存过了
    }

    [RelayCommand]
    private void AddTag(WorkTag tag)
    {
        if (!WorkTags.Contains(tag))
            WorkTags.Add(tag);
    }

    [RelayCommand]
    private void DelTag(WorkTag tag)
    {
        WorkTags.Remove(tag);
    }
    
    private void UpdateAvailableTags()
    {
        AvailableTags.Clear();
        if (WorkTags.Count > 0)
        {
            // show only secondary tags
            foreach (var tag in AllTags.Where(x => x.Level == TagLevels.Secondary))
            {
                if (!WorkTags.Contains(tag))
                    AvailableTags.Add(tag);
            }
        }
        else
        {
            // show only primary tags
            foreach (var tag in AllTags.Where(x => x.Level == TagLevels.Primary))
            {
                AvailableTags.Add(tag);
            }
        }
    }

    private bool CanUpload()
    {
        return IssueIndex >= 0 && ActivityIndex >= 0; // new item and both set
    }

    public async Task<bool> Upload()
    {
        if (Uploaded)
            return false;
        if (!CanUpload())
            return false;
        // 先保存一下
        // Save(out _);
        Debug.Assert(WorkItem is not null);
        Debug.Assert(TimeEntry is not null);
        TimeEntry.EntryId = await Task.Run(() => RedMineApis.CreateTimeEntry(out var ti, TimeEntry.IssueId, TimeEntry.ActivityId, WorkItem.CreateDate,
            WorkItem.Time, WorkItem.Comment) ? ti.Id : 0);
        Db!.UpdateWorkTimeEntry(TimeEntry); // 关联到数据库
        Uploaded = TimeEntry.EntryId > 0;
        return Uploaded;
    }
}