using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.App.Models;
using Diary.App.Utils;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using Ursa.Controls;

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
        var config = App.Current.AppConfig;
        SettingTreeBuilder.BuildTree(SettingsTree, config);
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
        var confirm = await MessageBox.ShowOverlayAsync(
            message: "所做的所有更改均被丢弃",
            title: "确认执行吗？",
            icon: MessageBoxIcon.Warning,
            button: MessageBoxButton.OKCancel
        );
        _logger.LogDebug("Result: {confirm}", confirm);
        if (confirm != MessageBoxResult.OK)
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