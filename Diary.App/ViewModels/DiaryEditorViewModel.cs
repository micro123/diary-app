using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Utils;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class DiaryEditorViewModel : ViewModelBase
{
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SelectTodayCommand))]
    private DateTime _selectedDate;

    private DateTime _currentDate;
    private DateTime CurrentDate {get => _currentDate; set => SetProperty(ref _currentDate, value);}

    [ObservableProperty] private ObservableCollection<string> _dailyItems = new();
    
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
        ObservableCollection<string> now = new();
        for (int i = 0; i < 10; ++i)
        {
            now.Add($"daily item #{i} for {value}");
        }
        DailyItems = now;
    }

    public DiaryEditorViewModel()
    {
        SelectedDate = DateTime.Today;
    }
}