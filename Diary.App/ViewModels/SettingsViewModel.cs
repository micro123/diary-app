using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.GUIBase;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;
using Diary.App.ViewModels.Dialogs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Ursa.Controls;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ILogger _logger;
    private readonly TrackerPluginDiagnosticsService _diagnostics;
    private readonly DiagnosticLogExportService _logExport;
    private readonly IServiceProvider _services;
    [ObservableProperty] private SettingGroup _settingsTree = new("Root");
    public SettingsViewModel(
        ILogger logger,
        TrackerPluginDiagnosticsService diagnostics,
        DiagnosticLogExportService logExport,
        IEnumerable<ITrackerConfigurationProvider> configurationProviders,
        IServiceProvider services)
    {
        _logger = logger;
        _diagnostics = diagnostics;
        _logExport = logExport;
        _services = services;
        _logger.LogDebug("Tracker 配置提供者：{Count} 个", configurationProviders.Count());
        BuildTree();
    }

    private void BuildTree()
    {
        var app = BaseApp.Instance;
        SettingTreeBuilder.BuildTree(SettingsTree, app.AppConfig, app);
    }

    [RelayCommand]
    private async Task ShowTrackerDiagnostics()
    {
        var viewModel = _services.GetRequiredService<TrackerPluginDiagnosticsViewModel>();
        var options = new OverlayDialogOptions
        {
            CanDragMove = false,
            CanResize = true,
            CanLightDismiss = false,
            IsCloseButtonVisible = false,
        };
        await OverlayDialog.ShowCustomModal<object>(viewModel, options: options);
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

    [RelayCommand]
    private async Task ShowTrackerSettings()
    {
        var viewModel = _services.GetRequiredService<TrackerSettingsDialogViewModel>();
        await OverlayDialog.ShowCustomModal<object>(viewModel, options: new OverlayDialogOptions
        {
            CanDragMove = false,
            CanResize = true,
            CanLightDismiss = false,
            IsCloseButtonVisible = false,
        });
    }
}
