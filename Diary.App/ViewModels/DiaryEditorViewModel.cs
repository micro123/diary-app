using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.Core.Constants;
using Diary.App.Messages;
using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class DiaryEditorViewModel : ViewModelBase
{
    private readonly ILogger _logger;

    [ObservableProperty]
    private DateTime _selectedDate;

    private DateTime _currentDate;

    private DateTime CurrentDate
    {
        get => _currentDate;
        set => SetProperty(ref _currentDate, value);
    }
    
    private string CurrentDateString => TimeTools.FormatDateTime(CurrentDate);

    [RelayCommand]
    private void NewWorkItem()
    {
        SelectedWork = new WorkEditorViewModel()
        {
            Date = CurrentDateString,
        };
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveWorkItem()
    {
    }

    private bool CanSave => SelectedWork != null;

    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void DuplicateWorkItem()
    {
    }

    private bool CanDuplicate => SelectedWork != null;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void DeleteWorkItem()
    {
    }

    private bool CanDelete => SelectedWork != null && SelectedWork.CanDelete();

    [RelayCommand]
    private void SelectToday()
    {
        var today = DateTime.Today;
        SetProperty(ref _currentDate, today, nameof(CurrentDate));
        SelectedDate = today;
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        _currentDate = value;
        _logger.LogDebug("date changed to {0}", _currentDate);
        FetchWorks();
    }

    public DiaryEditorViewModel(ILogger logger)
    {
        _logger = logger;
        SelectedDate = DateTime.Today;
    }

    private void FetchWorks()
    {
        DailyWorks.Clear();
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