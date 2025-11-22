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

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SelectTodayCommand))]
    private DateTime _selectedDate;

    private DateTime _currentDate;
    private DateTime CurrentDate {get => _currentDate; set => SetProperty(ref _currentDate, value);}

    [RelayCommand]
    void Test(object parameter)
    {
        NotificationManager?.Show(parameter, NotificationType.Information);
    }
    
    [RelayCommand(CanExecute = nameof(CanGoToday))]
    void SelectToday()
    {
        var today = DateTime.Today;
        SetProperty(ref _currentDate, today, nameof(CurrentDate));
        SelectedDate = today;
    }

    private bool CanGoToday()
    {
        return SelectedDate != DateTime.Today;
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        _currentDate = value;
        _logger.LogDebug("date changed to {0}", _currentDate);
        // TODO: fetch db
        ObservableCollection<WorkEditorViewModel> dailyWorks = new();
        foreach (var i in Enumerable.Range(1, 10))
        {
            dailyWorks.Add(new WorkEditorViewModel());
        }
        DailyWorks = dailyWorks;
    }

    public DiaryEditorViewModel(ILogger logger)
    {
        _logger = logger;
        SelectedDate = DateTime.Today;
    }

    #region 编辑器数据

    [ObservableProperty] private ObservableCollection<WorkEditorViewModel> _dailyWorks = new();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem))]
    private WorkEditorViewModel? _selectedWork;
    public bool HasItem => SelectedWork != null;

    #endregion
}