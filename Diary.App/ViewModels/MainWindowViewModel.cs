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
using Diary.Core.Constants;
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
    public string VersionString { get; } = $"{DataVersion.VersionString}.{VersionInfo.CommitCount}-{VersionInfo.GitVersionShort}";

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
                    await MessageBox.ShowOverlayAsync(msg.Body, msg.Title);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "error");
                }
            });
        });
        
        Messenger.Register<RunCommandEvent>(this, (r, m) => { HandleCommand(m.Value); });
        
        Messenger.Register<ToastEvent>(this, (r, m) =>
        {
            ToastManager?.Show(m.Value);
        });
    }

    private void HandleCommand(string cmd)
    {
        switch (cmd)
        {
            case CommandNames.ShowDbSettings:
                Dispatcher.UIThread.Post(async () =>
                {
                    var options = new OverlayDialogOptions()
                    {
                        CanDragMove = false,
                        CanResize = false,
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
            case CommandNames.EditWorkTags:
                Dispatcher.UIThread.Post(async () =>
                {
                    var options = new OverlayDialogOptions()
                    {
                        CanDragMove = false,
                        CanResize = false,
                        CanLightDismiss = false,
                        Mode = DialogMode.None,
                        IsCloseButtonVisible = false,
                    };
                    var vm = _serviceProvider.GetRequiredService<TagEditorViewModel>();
                    await OverlayDialog.ShowCustomModal<object>(vm, options: options);
                });
                return;
        }

        throw new ArgumentOutOfRangeException(nameof(cmd));
    }

    private bool _quiting;
    
    [RelayCommand]
    private void Quit()
    {
        _quiting = true;
        (View as Window)?.Close();
    }

    [RelayCommand(CanExecute = nameof(CanMinimized))]
    private void Minimized()
    {
        if (View is Window window)
            window.WindowState = WindowState.Minimized;
    }

    private bool CanMinimized()
    {
        return (View as Window)?.WindowState != WindowState.Minimized;
    }
    
    [RelayCommand(CanExecute = nameof(CanMaximized))]
    private void Maximized()
    {
        if (View is Window window)
            window.WindowState = WindowState.Maximized;
    }

    private bool CanMaximized()
    {
        return (View as Window)?.WindowState != WindowState.Maximized;
    }

    protected override void OnAttachView(Control? view)
    {
        Window? window = view as Window;
        window!.PropertyChanged += (sender, args) =>
        {
            if (args.Property == Window.WindowStateProperty)
            {
                MinimizedCommand.NotifyCanExecuteChanged();
                MaximizedCommand.NotifyCanExecuteChanged();
            }
        };
    }

    [RelayCommand]
    private void Closing(object? parameter)
    {
        if (_quiting)
            return;
        if (parameter is WindowClosingEventArgs args)
        {
            args.Cancel = true;
            _logger.LogInformation("reject window closing!");
        }
    }
}