using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.App.Utils;
using Diary.Core.Constants;
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
        var messenger = WeakReferenceMessenger.Default;
        messenger.Register<WindowStateEvent>(this, (r, m) =>
        {
            if (!App.Current.AppConfig.ViewSettings.AlwaysShowTrayIcon)
                AppHidden = !m.Value;
        });
        messenger.Register<ConfigUpdateEvent>(this, (r, m) =>
        {
            // 能进这里那么主窗口一定可见
            if (!App.Current.AppConfig.ViewSettings.AlwaysShowTrayIcon)
                AppHidden = false;
        });
    }

    [ObservableProperty]
    private bool _appHidden = App.Current.AppConfig.ViewSettings.AlwaysShowTrayIcon;

    [RelayCommand]
    private void QuitApp()
    {
        _logger.LogInformation("Quitting app");
        EventDispatcher.RunCommand(CommandNames.QuitApp);
    }

    [RelayCommand]
    private void ShowAbout()
    {
        _logger.LogInformation("Showing about");
        EventDispatcher.RunCommand(CommandNames.ShowAboutDialog);
    }

    [RelayCommand]
    private void RaiseWindow()
    {
        _logger.LogInformation("Raise window");
        EventDispatcher.RunCommand(CommandNames.RaiseMainWindow);
    }
}