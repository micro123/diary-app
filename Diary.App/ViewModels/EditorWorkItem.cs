using CommunityToolkit.Mvvm.ComponentModel;
using Diary.Core.Data.Base;
using Diary.Utils;

namespace Diary.App.ViewModels;

public partial class WorkEditorViewModel: ViewModelBase
{
    // db data fields
    private WorkItem? WorkItem { get; set; } // ref to existed db item, may null
    
    // generic data
    [ObservableProperty] private string _date;
    [ObservableProperty] private string _comment;
    [ObservableProperty] private string _note;
    [ObservableProperty] private double _time;
    [ObservableProperty] private WorkPriorities _priority;
    
    // todo: redmine date
    
    // todo: plm?
    
    public static WorkEditorViewModel FromWorkItem(WorkItem workItem)
    {
        return new WorkEditorViewModel
        {
            WorkItem = workItem,
            Date = workItem.Date,
            Comment = workItem.Comment,
            Time = workItem.Time,
            Priority = workItem.Priority,
        };
    }

    public WorkEditorViewModel()
    {
        Date = TimeTools.Today();
        Comment = App.Current.AppConfig.WorkSettings.DefaultTaskTitle;
        Note = string.Empty;
        Time = 0.0;
        Priority = WorkPriorities.P0;
    }

    public void Save()
    {
        if (WorkItem != null)
        {
            // todo: update exists
        }
        else
        {
            // create new
        }
    }

    public void Delete()
    {
        // remove from db
    }

    public bool CanDelete()
    {
        // todo: check if commited
        return true;
    }
}