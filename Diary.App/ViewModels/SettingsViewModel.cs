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
using Microsoft.Extensions.Logging;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class SettingsViewModel : ViewModelBase, IDialogContext
{
    private readonly ILogger _logger;
    private readonly DiagnosticLogExportService _logExport;
    [ObservableProperty] private SettingGroup _settingsTree = new("Root");
    public SettingsViewModel(
        ILogger logger,
        DiagnosticLogExportService logExport,
        IEnumerable<ITrackerConfigurationProvider> configurationProviders)
    {
        _logger = logger;
        _logExport = logExport;
        _logger.LogDebug("Tracker 配置提供者：{Count} 个", configurationProviders.Count());
        BuildTree();
    }

    public event EventHandler<object?>? RequestClose;

    public void Close() => RequestClose?.Invoke(this, null);

    private void BuildTree()
    {
        var app = BaseApp.Instance;
        SettingTreeBuilder.BuildTree(SettingsTree, app.AppConfig, app);
        SettingsTree.Load();
    }

    [RelayCommand]
    private void Save()
    {
        SettingsTree.Save();
        NotificationManager?.Show("已保存", NotificationType.Success);
        Messenger.Send(new ConfigUpdateEvent());
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void OpenCurrentLog()
    {
        try
        {
            _logger.LogInformation("用户请求打开当前日志文件");
            var path = _logExport.GetCurrentLogFile();
            if (path is null)
            {
                NotificationManager?.Show("没有可打开的日志", NotificationType.Information);
                return;
            }

            ProcUtils.OpenFileCrossPlatform(path);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "打开当前日志文件失败");
            NotificationManager?.Show("打开当前日志文件失败", NotificationType.Error);
        }
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
        // if (!await EventDispatcher.Confirm("确认执行吗？", "所做的所有更改均被丢弃！"))
        //     return;

        ForceLoad();

        NotificationManager?.Show("更改已丢弃!", NotificationType.Information);
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void ForceLoad()
    {
        SettingsTree.Load();
    }

}
