using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class MainWindowViewModel : ViewModelBase
{
    public string VersionString { get; } = $"{BuildInfo.GitHash}";

    [RelayCommand]
    private void CopyVersion()
    {
        Console.WriteLine("Copy Version");
    }

    [ObservableProperty] private ObservableCollection<NavigateInfo> _pages;

    [ObservableProperty] private NavigateInfo? _selectedPage = null;

    partial void OnSelectedPageChanged(NavigateInfo? value)
    {
        CurrentPageModel = value?.ViewModel;
    }

    [ObservableProperty] private ViewModelBase? _currentPageModel = null;

    public MainWindowViewModel(IServiceProvider serviceProvider)
    {
        _pages =
        [
            new NavigateInfo("日记", "mdi-notebook", serviceProvider.GetService<DiaryEditorViewModel>()),
            new NavigateInfo("RedMine", "fa-cloud", serviceProvider.GetRequiredService<RedMineManageViewModel>()),
            new NavigateInfo("统计", "fa-chart-pie", serviceProvider.GetRequiredService<StatisticsViewModel>()),
            new NavigateInfo("调查", "mdi-chat-processing-outline", serviceProvider.GetRequiredService<SurveyViewModel>()),
            new NavigateInfo("设置", "mdi-cog-outline", serviceProvider.GetService<SettingsViewModel>())
        ];

        SelectedPage = Pages[0];
    }
}