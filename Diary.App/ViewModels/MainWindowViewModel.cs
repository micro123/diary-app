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
using Diary.Core.Data.AppConfig;
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
    public string VersionString => AppInfo.AppVersionString;

    public string VersionDetails => AppInfo.AppVersionDetails;

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
                var evt = m.Value;
                var vm = _serviceProvider.GetRequiredService<StandardMessageViewModel>();
                vm.Body = evt.Body;
                var options = new OverlayDialogOptions()
                {
                    Title = evt.Title,
                    CanDragMove = false,
                    CanResize = false,
                    CanLightDismiss = evt.LightDismiss,
                    Mode = evt.Mode,
                    Buttons = evt.Button,
                };
                
                if (m.Value.Modal)
                    await OverlayDialog.ShowModal<StandardMessageView, StandardMessageViewModel>(vm, options: options);
                else
                    OverlayDialog.Show<StandardMessageView, StandardMessageViewModel>(vm, options: options);
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
            case CommandNames.RaiseMainWindow:
                Dispatcher.UIThread.Post(() =>
                {
                    if (!Window!.IsVisible)
                    {
                        Window.Show();
                    }
                    else
                    {
                        Window.Activate();
                    }
                });
                return;
            case CommandNames.QuitApp:
                Dispatcher.UIThread.Post(Quit);
                return;
            case CommandNames.ShowAboutDialog:
                Dispatcher.UIThread.Post(ShowAbout);
                return;
            
        }

        throw new ArgumentOutOfRangeException(nameof(cmd));
    }

    private bool _quiting;
    private Window? Window => View as Window;
    
    [RelayCommand]
    private void Quit()
    {
        _quiting = true;
        (View as Window)?.Close();
    }

    [RelayCommand(CanExecute = nameof(CanMinimized))]
    private void Minimized()
    {
        Window!.WindowState = WindowState.Minimized;
    }

    private bool CanMinimized()
    {
        return Window?.WindowState != WindowState.Minimized;
    }
    
    [RelayCommand(CanExecute = nameof(CanMaximized))]
    private void Maximized()
    {
            Window!.WindowState = WindowState.Maximized;
    }

    private bool CanMaximized()
    {
        return Window?.WindowState != WindowState.Maximized;
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
        if (!AllConfig.Instance.ViewSettings.HideToTray)
            return;
        if (parameter is WindowClosingEventArgs args)
        {
            args.Cancel = true;
            Window!.Hide();
            Messenger.Send(new WindowStateEvent(false));
        }
    }

    [RelayCommand]
    private void Opened(object? parameter)
    {
        Messenger.Send(new WindowStateEvent(true));
    }

    [RelayCommand]
    private void ShowAbout()
    {
        if (!Window!.IsVisible)
            Window.Show();
        var options = new OverlayDialogOptions()
        {
            Title = "关于",
            Mode = DialogMode.Info,
            Buttons = DialogButton.OK,
            CanDragMove = false,
            CanLightDismiss = true,
            IsCloseButtonVisible = true,
        };
        OverlayDialog.Show(_serviceProvider.GetRequiredService<AboutViewModel>(), null, options);
    }
}