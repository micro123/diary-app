using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.App.Utils;
using Diary.Core.Data.Base;
using Diary.Core.Data.Display;
using Diary.Database;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.App.ViewModels;

public partial class WorkEditorViewModel : ViewModelBase
{
    private readonly DbShareData _shareData;

    // db data fields
    private WorkItem? WorkItem { get; set; } // ref to existed db item, may null

    // generic data
    [ObservableProperty] private string _date;
    [ObservableProperty] private string _comment;
    [ObservableProperty] private string _note;
    [ObservableProperty] private double _time;
    [ObservableProperty] private WorkPriorities _priority;
    [ObservableProperty] private ObservableCollection<WorkTag> _workTags = new();

    public ObservableCollection<WorkTag> AllTags => _shareData.WorkTags;
    public ObservableCollection<RedMineIssueDisplay> RedMineIssues => _shareData.RedMineIssues;

    // todo: redmine date

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
            if (WorkItem is { Id: > 0 })
            {
                if (_syncing_tags)
                    return;
                // 这里处理添加删除标签
                switch (args.Action)
                {
                    case NotifyCollectionChangedAction.Add:
                        Db!.WorkItemAddTag(WorkItem, (WorkTag)args.NewItems![0]!);
                        break;
                    case NotifyCollectionChangedAction.Remove:
                        Db!.WorkItemRemoveTag(WorkItem, (WorkTag)args.OldItems![0]!);
                        break;
                }
            }
        };
    }

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

    public void SyncNote()
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
            var tags = Db!.GetWorkItemTags(WorkItem);
            WorkTags.Clear();
            foreach (var tag in tags)
            {
                WorkTags.Add(tag);
            }
        }
        _syncing_tags = false;
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
}