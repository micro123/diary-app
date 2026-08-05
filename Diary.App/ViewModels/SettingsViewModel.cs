using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using Diary.GUIBase;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly TrackerPluginDiagnosticsService _diagnostics;
    private readonly DiagnosticLogExportService _logExport;
    [ObservableProperty] private SettingGroup _settingsTree = new("Root");
    [ObservableProperty]
    private ObservableCollection<TrackerPluginDiagnosticViewModel> _pluginDiagnostics = new();

    public SettingsViewModel(
        ILogger logger,
        TrackerPluginDiagnosticsService diagnostics,
        DiagnosticLogExportService logExport)
    {
        _logger = logger;
        _diagnostics = diagnostics;
        _logExport = logExport;
        BuildTree();
        RefreshDiagnostics();
    }

    private void RefreshDiagnostics()
    {
        PluginDiagnostics = new ObservableCollection<TrackerPluginDiagnosticViewModel>(
            _diagnostics.GetSnapshot().Select(entry =>
                new TrackerPluginDiagnosticViewModel(
                    entry,
                    () => Retry(entry.PluginId, entry.InstanceId!),
                    () => Toggle(entry.PluginId, entry.InstanceId!, entry.InstanceState != TrackerInstanceState.Enabled))));
    }

    private void Retry(string pluginId, string instanceId)
    {
        var success = _diagnostics.Retry(pluginId, instanceId);
        RefreshDiagnostics();
        NotificationManager?.Show(
            success ? "插件实例已恢复" : "插件实例重试失败",
            success ? NotificationType.Success : NotificationType.Error);
    }

    private void Toggle(string pluginId, string instanceId, bool enabled)
    {
        var success = _diagnostics.SetInstanceEnabled(pluginId, instanceId, enabled);
        RefreshDiagnostics();
        NotificationManager?.Show(
            success ? (enabled ? "插件实例已启用" : "插件实例已禁用") : "插件实例状态更新失败",
            success ? NotificationType.Success : NotificationType.Error);
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
    private void ExportLogs()
    {
        var path = _logExport.Export();
        NotificationManager?.Show(
            path is null ? "没有可导出的日志" : $"日志已导出：{path}",
            path is null ? NotificationType.Information : NotificationType.Success);
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
