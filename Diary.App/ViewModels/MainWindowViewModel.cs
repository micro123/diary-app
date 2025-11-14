using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.Core;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class MainWindowViewModel : ViewModelBase
{
    public string VersionString { get; } = $"{DataVersion.VersionString}.{VersionInfo.CommitCount}";

    public string VersionDetails { get; } = $"""
                                             数据版本：{DataVersion.VersionString} ({DataVersion.VersionCode:X8})
                                             编译增量：{VersionInfo.CommitCount}
                                             Git分支：{VersionInfo.CommitCount}
                                             Git提交：{VersionInfo.GitVersionFull}
                                             提交消息：{VersionInfo.LastCommitMessage}
                                             提交时间：{VersionInfo.LastCommitDate}
                                             编译时间：{VersionInfo.BuildTime}
                                             编译主机：{VersionInfo.HostName}
                                             """;

    [RelayCommand]
    private async Task CopyVersion(bool simple)
    {
        await CopyStringToClipboardAsync(simple ? VersionString : VersionDetails);
        NotificationManager?.Show("已复制", NotificationType.Success);
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