using System.Collections.ObjectModel;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.Utils;

namespace Diary.App.ViewModels.Dialogs;

[DiAutoRegister]
public partial class TrackerPluginDiagnosticsViewModel : ViewModelBase
{
    private readonly TrackerPluginDiagnosticsService _diagnostics;

    [ObservableProperty]
    private ObservableCollection<TrackerPluginDiagnosticViewModel> _entries = new();

    public TrackerPluginDiagnosticsViewModel(TrackerPluginDiagnosticsService diagnostics)
    {
        _diagnostics = diagnostics;
        Refresh();
    }

    public bool IsEmpty => Entries.Count == 0;

    private void Refresh()
    {
        Entries = new ObservableCollection<TrackerPluginDiagnosticViewModel>(
            _diagnostics.GetSnapshot().Select(entry =>
                new TrackerPluginDiagnosticViewModel(
                    entry,
                    () => Retry(entry.PluginId, entry.InstanceId!),
                    () => Toggle(entry.PluginId, entry.InstanceId!, entry.InstanceState != TrackerInstanceState.Enabled),
                    () => Uninstall(entry.PluginId, entry.InstanceId!, deleteData: false),
                    () => Uninstall(entry.PluginId, entry.InstanceId!, deleteData: true))));
    }

    private void Retry(string pluginId, string instanceId)
    {
        var success = _diagnostics.Retry(pluginId, instanceId);
        Refresh();
        NotificationManager?.Show(
            success ? "插件实例已恢复" : "插件实例重试失败",
            success ? NotificationType.Success : NotificationType.Error);
    }

    private void Toggle(string pluginId, string instanceId, bool enabled)
    {
        var success = _diagnostics.SetInstanceEnabled(pluginId, instanceId, enabled);
        Refresh();
        NotificationManager?.Show(
            success ? (enabled ? "插件实例已启用" : "插件实例已禁用") : "插件实例状态更新失败",
            success ? NotificationType.Success : NotificationType.Error);
    }

    private async void Uninstall(string pluginId, string instanceId, bool deleteData)
    {
        if (deleteData && !await EventDispatcher.Confirm(
                "删除插件数据？",
                "该操作将删除此实例的本地数据，且无法恢复。"))
            return;

        var success = _diagnostics.UninstallInstance(pluginId, instanceId, deleteData);
        Refresh();
        NotificationManager?.Show(
            success
                ? (deleteData ? "插件实例已卸载并删除数据" : "插件实例已卸载，配置和数据已保留")
                : "插件实例卸载失败",
            success ? NotificationType.Success : NotificationType.Error);
    }
}
