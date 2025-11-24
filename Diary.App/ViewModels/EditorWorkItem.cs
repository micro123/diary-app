using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Utils;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.Utils;

namespace Diary.App.ViewModels;

public partial class WorkEditorViewModel : ViewModelBase
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

    private DbInterfaceBase? Db => App.Current.UseDb;

    public static WorkEditorViewModel FromWorkItem(WorkItem workItem)
    {
        return new WorkEditorViewModel
        {
            WorkItem = workItem,
            Date = workItem.CreateDate,
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
        
        db.UpdateWorkItem(WorkItem);
        
        if (!string.IsNullOrWhiteSpace(Note))
        {
            db.WorkUpdateNote(WorkItem, Note);
        }
        else
        {
            db.WorkDeleteNote(WorkItem);
        }
    }

    public void Delete()
    {
        // remove from db
        Db!.DeleteWorkItem(WorkItem!);
    }

    public bool CanDelete()
    {
        // todo: check if commited
        return WorkItem!=null && WorkItem.Id!=0;
    }

    [RelayCommand]
    private void QuickDate(string what)
    {
        switch (what)
        {
            case  "0": Date = TimeTools.Today(); break;
            case "+1": Date = TimeTools.Tomorrow(); break;
            case "-1": Date = TimeTools.Yestoday(); break;
        }
    }

    public void SyncNote()
    {
        if (WorkItem != null && WorkItem.Id > 0)
        {
            Note = Db!.WorkGetNote(WorkItem!) ?? string.Empty;
        }
    }

    public WorkEditorViewModel? Clone()
    {
        throw  new System.NotImplementedException();
    }
}