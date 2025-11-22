using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Constants;
using Diary.App.Dialogs;
using Diary.App.Messages;
using Diary.App.Models;
using Diary.Core;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ursa.Controls;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    public string VersionString { get; } = $"{DataVersion.VersionString}.{VersionInfo.CommitCount}";

    public string VersionDetails { get; } = $"""
                                             数据版本：{DataVersion.VersionString} (0x{DataVersion.VersionCode:X8})
                                             编译增量：{VersionInfo.CommitCount}
                                             Git分支：{VersionInfo.Branch}
                                             Git提交：{VersionInfo.GitVersionShort}
                                             提交消息：{VersionInfo.LastCommitMessage}
                                             提交时间：{VersionInfo.LastCommitDate}
                                             编译时间：{VersionInfo.BuildTime}
                                             编译主机：{VersionInfo.HostName}
                                             """;

    [RelayCommand]
    private async Task CopyVersion(bool simple)
    {
        await CopyStringToClipboardAsync(simple ? VersionString : VersionDetails);
        ToastManager?.Show("已复制", NotificationType.Success);
    }

    [ObservableProperty] private ObservableCollection<NavigateInfo> _pages;

    [ObservableProperty] private NavigateInfo? _selectedPage = null;

    partial void OnSelectedPageChanged(NavigateInfo? value)
    {
        CurrentPageModel = value?.ViewModel;
    }

    [ObservableProperty] private ViewModelBase? _currentPageModel = null;

    public MainWindowViewModel(IServiceProvider serviceProvider, ILogger logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _pages =
        [
            new NavigateInfo(PageNames.DiaryEditor, "mdi-notebook", serviceProvider.GetService<DiaryEditorViewModel>()),
            new NavigateInfo(PageNames.RedMineTool, "fa-cloud",
                serviceProvider.GetRequiredService<RedMineManageViewModel>()),
            new NavigateInfo(PageNames.Statistics, "fa-chart-pie",
                serviceProvider.GetRequiredService<StatisticsViewModel>()),
            new NavigateInfo(PageNames.SurveyTool, "mdi-chat-processing-outline",
                serviceProvider.GetRequiredService<SurveyViewModel>()),
            new NavigateInfo(PageNames.Settings, "mdi-cog-outline", serviceProvider.GetService<SettingsViewModel>())
        ];

        SelectedPage = Pages[0];

        Messenger.Register<PageSwitchEvent>(this, (r, m) =>
        {
            var page = Pages.FirstOrDefault(x => x.Name == m.Value);
            if (page is not null)
            {
                SelectedPage = page;
            }
        });

        Messenger.Register<ConfigUpdateEvent>(this, (r, m) => { _logger.LogInformation("config updated!"); });

        Messenger.Register<NotifyEvent>(this, (r, m) =>
        {
            Dispatcher.UIThread.Post(async void () =>
            {
                try
                {
                    var msg = m.Value;
                    await MessageBox.ShowOverlayAsync(msg.Body, msg.Body);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "error");
                }
            });
        });
        Messenger.Register<RunCommandEvent>(this, (r, m) => { HandleCommand(m.Value); });
    }

    private void HandleCommand(string cmd)
    {
        if (cmd == "SHOW_DB_SETTINGS")
        {
            Dispatcher.UIThread.Post(async () =>
            {
                var options = new OverlayDialogOptions()
                {
                    CanDragMove = false,
                    CanResize = true,
                    CanLightDismiss = false,
                    Mode = DialogMode.None,
                    IsCloseButtonVisible = false,
                };
                var vm = _serviceProvider.GetRequiredService<GenericConfigViewModel>();
                vm.InitSettings("数据库设置", App.Current.UseDb!.Config);
                bool result = await OverlayDialog.ShowCustomModal<bool>(vm, options: options);
                _logger.LogInformation($"db settings updated: {result}");
            });
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(cmd));
    }
}