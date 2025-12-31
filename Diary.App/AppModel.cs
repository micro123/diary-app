using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.Core.Constants;
using Diary.GUIBase;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App;

[DiAutoRegister]
public partial class AppModel: ObservableObject
{
    private readonly ILogger _logger;

    public AppModel(ILogger logger)
    {
        _logger = logger;
        var appBase = BaseApp.Instance;
        var messenger = WeakReferenceMessenger.Default;
        messenger.Register<WindowStateEvent>(this, (r, m) =>
        {
            if (!appBase.AppConfig.ViewSettings.AlwaysShowTrayIcon)
                TrayVisible = !m.Value;
        });
        messenger.Register<ConfigUpdateEvent>(this, (r, m) =>
        {
            // 能进这里那么主窗口一定可见
            if (!appBase.AppConfig.ViewSettings.AlwaysShowTrayIcon)
                TrayVisible = false;
        });
        TrayVisible = appBase.AppConfig.ViewSettings.AlwaysShowTrayIcon;
    }

    [ObservableProperty]
    private bool _trayVisible;

    [RelayCommand]
    private void QuitApp()
    {
        EventDispatcher.RunCommand(CommandNames.QuitApp);
    }

    [RelayCommand]
    private void ShowAbout()
    {
        EventDispatcher.RunCommand(CommandNames.ShowAboutDialog);
    }

    [RelayCommand]
    private void RaiseWindow()
    {
        EventDispatcher.RunCommand(CommandNames.RaiseMainWindow);
    }
}