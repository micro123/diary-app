using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Utils;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class StatusBarViewModel: ViewModelBase
{
    [ObservableProperty] private string _date;
    [ObservableProperty] private string _userName;
    [ObservableProperty] private string _computerName;
    [ObservableProperty] private bool _hasTasks;

    public StatusBarViewModel()
    {
        _userName = SysInfo.GetUsername();
        _computerName = SysInfo.GetHostname();
        _date = DateTime.Now.ToShortDateString();
        _hasTasks = false;
    }

    [RelayCommand]
    private void OpenSourceUrl()
    {
        ProcUtils.OpenUrlCrossPlatform("https://github.com/micro123/diary-app");
    }
}