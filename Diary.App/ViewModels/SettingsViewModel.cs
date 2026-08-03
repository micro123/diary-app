using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.GUIBase;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    [ObservableProperty] private SettingGroup _settingsTree = new("Root");

    public SettingsViewModel(ILogger logger)
    {
        _logger = logger;
        BuildTree();
    }

    private void BuildTree()
    {
        var app = BaseApp.Instance;
        SettingTreeBuilder.BuildTree(SettingsTree, app.AppConfig, app);
    }

    [RelayCommand]
    private void Save()
    {
        SettingsTree.Save();
        NotificationManager?.Show("已保存", NotificationType.Success);
        Messenger.Send(new ConfigUpdateEvent());
    }

    [RelayCommand]
    private async Task Load()
    {
        // var confirm = await MessageBox.ShowOverlayAsync(
        //     message: "所做的所有更改均被丢弃",
        //     title: "确认执行吗？",
        //     icon: MessageBoxIcon.Warning,
        //     button: MessageBoxButton.OKCancel
        // );
        // _logger.LogDebug("Result: {confirm}", confirm);
        // if (confirm != MessageBoxResult.OK)
        //     return;
        if (!await EventDispatcher.Confirm("确认执行吗？", "所做的所有更改均被丢弃！"))
            return;

        ForceLoad();

        NotificationManager?.Show("更改已丢弃!", NotificationType.Information);
    }

    [RelayCommand]
    private void ForceLoad()
    {
        SettingsTree.Load();
    }
}