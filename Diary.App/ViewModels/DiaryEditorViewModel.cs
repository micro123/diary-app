using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.App.Models;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class DiaryEditorViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;

    [ObservableProperty]
    private DateTime _selectedDate;
    [ObservableProperty]
    private DateTime _currentDate;
    
    private string CurrentDateString => TimeTools.FormatDateTime(CurrentDate);
    private bool _creating;

    [RelayCommand]
    private void NewWorkItem()
    {
        _creating = true;
        SelectedWork = null; // hack: clear selection
        SelectedWork = new WorkEditorViewModel(_serviceProvider.GetRequiredService<DbShareData>())
        {
            Date = CurrentDateString,
        };
        _creating = false;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveWorkItem()
    {
        SelectedWork!.Save(out var created);
        if (created)
        {
            var bak = SelectedWork;
            SelectedWork = null;
            DailyWorks.Add(bak);
            SelectedWork = bak;
        }
    }

    private bool CanSave => SelectedWork != null;

    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void DuplicateWorkItem()
    {
        // duplicate but not save
        var item = SelectedWork!.Clone();
        SelectedWork = null;
        _creating = true;
        SelectedWork = item;
        _creating = false;
    }

    private bool CanDuplicate => SelectedWork != null && SelectedWork.CanClone();
    private bool _deleting;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteWorkItem()
    {
        _deleting = true;
        SelectedWork!.Delete();
        DailyWorks.Remove(SelectedWork!);
        SelectedWork = DailyWorks.FirstOrDefault();
        _deleting = false;
    }

    private bool CanDelete => SelectedWork != null && SelectedWork.CanDelete();

    [RelayCommand]
    private void SelectToday()
    {
        var today = DateTime.Today;
        CurrentDate = today;
        SelectedDate = today;
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        _currentDate = value;
        _logger.LogDebug("date changed to {0}", _currentDate);
        FetchWorks();
    }

    partial void OnSelectedWorkChanging(WorkEditorViewModel? value) // 指 即将 从 当前值 更改为 value
    {
        if (!_deleting && !_creating && SelectedWork is not null)
            SaveWorkItem();
        // fetch new
        value?.SyncNote();
        value?.SyncTags();
    }

    public DiaryEditorViewModel(ILogger logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        SelectedDate = DateTime.Today;
        
        Messenger.Register<DbChangedEvent>(this, (r, m) =>
        {
            if ((m.Value & DbChangedEvent.ShareData) != 0)
            {
                SelectedWork?.SyncTags(); // 重新拉取一次标签
            }
        });
    }

    private void FetchWorks()
    {
        DailyWorks.Clear();
        var db = App.Current.UseDb;
        if (db != null)
        {
            var dbItems = db.GetWorkItemByDate(CurrentDateString);
            foreach (var item in dbItems)
            {
                DailyWorks.Add(WorkEditorViewModel.FromWorkItem(item));
            }
        }
        else
        {
            _logger.LogWarning("db is null");
        }

        if (DailyWorks.Count > 0)
        {
            SelectedWork = DailyWorks[0];
        }
    }

    #region 编辑器数据

    [ObservableProperty] private ObservableCollection<WorkEditorViewModel> _dailyWorks = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem))]
    [NotifyCanExecuteChangedFor(nameof(SaveWorkItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateWorkItemCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteWorkItemCommand))]
    private WorkEditorViewModel? _selectedWork;

    public bool HasItem => SelectedWork != null;

    #endregion
}