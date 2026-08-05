using Avalonia.Controls.Notifications;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.GUIBase;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.Core.Utils;
using Diary.PluginBase;
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
    private readonly IReadOnlyList<(object Configuration, ITrackerConfigurationProvider Provider)> _pluginSettings;
    [ObservableProperty] private SettingGroup _settingsTree = new("Root");
    public ObservableCollection<ViewModelBase> PluginSettingsPages { get; } = new();
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
        _pluginSettings = LoadPluginSettings(configurationProviders);
        _logger.LogDebug("设置页加载插件配置：{Count} 个", _pluginSettings.Count);
        BuildTree();
    }

    private void BuildTree()
    {
        var app = BaseApp.Instance;
        SettingTreeBuilder.BuildTree(SettingsTree, app.AppConfig, app);
        foreach (var (configuration, provider) in _pluginSettings)
        {
            var page = provider.CreateSettingsPage(configuration);
            _logger.LogDebug("创建插件设置页：{PluginId}，配置类型：{ConfigurationType}，页面：{PageType}",
                provider.PluginId, configuration.GetType().Name, page?.GetType().Name ?? "通用设置");
            if (page is not null)
                PluginSettingsPages.Add(page);
            else
            {
                var group = new SettingGroup(provider.PluginId);
                SettingTreeBuilder.BuildTree(group, configuration, app);
                SettingsTree.Children.Add(group);
            }
        }
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
        foreach (var page in PluginSettingsPages.OfType<ITrackerSettingsPage>())
        {
            page.Save();
            page.Reload();
        }
        foreach (var (configuration, _) in _pluginSettings)
            if (!EasySaveLoad.Save(configuration))
                _logger.LogWarning("保存插件配置失败: {PluginId}", configuration.GetType().Name);
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
        foreach (var page in PluginSettingsPages.OfType<ITrackerSettingsPage>())
            page.Reload();
    }

    private static IReadOnlyList<(object Configuration, ITrackerConfigurationProvider Provider)> LoadPluginSettings(
        IEnumerable<ITrackerConfigurationProvider> providers)
    {
        if (BaseApp.Instance is not App app)
            return Array.Empty<(object, ITrackerConfigurationProvider)>();

        return providers
            .Where(provider => app.PluginConfigurations.TryGetValue(provider.PluginId, out _))
            .Select(provider => (app.PluginConfigurations[provider.PluginId], provider))
            .ToArray();
    }
}
