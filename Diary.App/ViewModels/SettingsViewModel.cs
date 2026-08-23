using Avalonia;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Fonts;
using Diary.App.Models;
using Diary.App.Services;
using Diary.GUIBase;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Core.Utils;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels;

[DiAutoRegister]
public partial class SettingsViewModel : ViewModelBase, IDialogContext
{
    private readonly ILogger _logger;
    private readonly DiagnosticLogExportService _logExport;
    private readonly AppFontService _fontService;
    private readonly McpSetupService _mcpSetupService;
    private McpStatusSetting _mcpStatusSetting = null!;
    private McpActionSetting _copyAiInstructionsSetting = null!;
    private McpActionSetting _copyGenericConfigurationSetting = null!;
    [ObservableProperty] private SettingGroup _settingsTree = new("Root");
    [ObservableProperty] private bool _hasMcpSnapshot;
    [ObservableProperty] private string _mcpSnapshotStatus = string.Empty;

    public SettingsViewModel(
        ILogger logger,
        DiagnosticLogExportService logExport,
        AppFontService fontService,
        McpSetupService mcpSetupService,
        IEnumerable<ITrackerConfigurationProvider> configurationProviders)
    {
        _logger = logger;
        _logExport = logExport;
        _fontService = fontService;
        _mcpSetupService = mcpSetupService;
        _logger.LogDebug("Tracker 配置提供者：{Count} 个", configurationProviders.Count());
        BuildTree();
        RefreshMcpStatus();
    }

    public event EventHandler<object?>? RequestClose;

    public void Close() => RequestClose?.Invoke(this, null);

    private void BuildTree()
    {
        var app = BaseApp.Instance;
        SettingTreeBuilder.BuildTree(SettingsTree, app.AppConfig, app);
        var mcpGroup = new SettingGroup(
            "AI 与 MCP",
            "生成可直接交给 AI 的 stdio MCP 配置说明；配置只引用用户确认过的只读快照，不包含数据库凭据或快照正文。");
        _mcpStatusSetting = new McpStatusSetting(
            "快照状态",
            "显示只读 MCP 快照是否存在及最后更新时间。");
        _copyAiInstructionsSetting = new McpActionSetting(
            "AI 配置说明",
            "复制包含 stdio 启动方式、工具列表和安全要求的 Markdown。",
            "复制 AI 说明",
            CopyMcpAiInstructionsCommand);
        _copyGenericConfigurationSetting = new McpActionSetting(
            "MCP JSON",
            "复制通用 mcpServers.diary command/args JSON 配置。",
            "复制 MCP JSON",
            CopyGenericMcpConfigurationCommand);
        mcpGroup.Children.Add(_mcpStatusSetting);
        mcpGroup.Children.Add(new McpActionSetting(
            "AI 上下文",
            "保存当前程序设置并打开 AI 上下文；不会自动生成快照。",
            "打开 AI 上下文",
            SaveAndOpenAiContextCommand,
            primary: true));
        mcpGroup.Children.Add(_copyAiInstructionsSetting);
        mcpGroup.Children.Add(_copyGenericConfigurationSetting);
        mcpGroup.Children.Add(new McpActionSetting(
            "使用文档",
            "打开 AI 脚本上下文与本地 MCP 使用指南。",
            "打开使用文档",
            OpenMcpGuideCommand));
        SettingsTree.Children.Add(mcpGroup);
        SettingsTree.Load();
    }

    [RelayCommand]
    private void Save()
    {
        if (!TrySaveSettings(out var fontResult))
            return;
        ShowSavedNotification(fontResult);
        Messenger.Send(new ConfigUpdateEvent());
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void SaveAndOpenAiContext()
    {
        if (!TrySaveSettings(out var fontResult))
            return;
        ShowSavedNotification(fontResult);
        Messenger.Send(new ConfigUpdateEvent());
        RequestClose?.Invoke(this, true);
        Messenger.Send(new OpenAiContextRequest());
    }

    [RelayCommand]
    private async Task CopyMcpAiInstructions()
    {
        if (!EnsureMcpSnapshot())
            return;
        if (await CopyStringToClipboardAsync(_mcpSetupService.CreateAiInstructions()))
            NotificationManager?.Show("给 AI 的 MCP 配置说明已复制", NotificationType.Success);
    }

    [RelayCommand]
    private async Task CopyGenericMcpConfiguration()
    {
        if (!EnsureMcpSnapshot())
            return;
        if (await CopyStringToClipboardAsync(_mcpSetupService.CreateGenericConfiguration()))
            NotificationManager?.Show("通用 MCP JSON 已复制", NotificationType.Success);
    }

    [RelayCommand]
    private void OpenMcpGuide()
    {
        if (!File.Exists(_mcpSetupService.GuidePath))
        {
            NotificationManager?.Show("安装目录中未找到 MCP 使用文档", NotificationType.Warning);
            return;
        }
        ProcUtils.OpenFileCrossPlatform(_mcpSetupService.GuidePath);
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

    public override void OnShow() => RefreshMcpStatus();

    private bool TrySaveSettings(out AppFontApplyResult fontResult)
    {
        fontResult = default!;
        try
        {
            SettingsTree.Save();
            if (!EasySaveLoad.Save(BaseApp.Instance.AppConfig))
                throw new InvalidOperationException("保存应用配置失败。");
            var application = Application.Current
                ?? throw new InvalidOperationException("应用尚未完成初始化，无法应用字体设置。");
            fontResult = _fontService.Apply(application, BaseApp.Instance.AppConfig.ViewSettings);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "保存设置失败");
            NotificationManager?.Show(exception.Message, NotificationType.Error);
            return false;
        }
    }

    private void ShowSavedNotification(AppFontApplyResult fontResult) =>
        NotificationManager?.Show(
            fontResult.UsedFallback ? $"已保存；{fontResult.Warning}" : "已保存，字体已立即生效",
            fontResult.UsedFallback ? NotificationType.Warning : NotificationType.Success);

    private bool EnsureMcpSnapshot()
    {
        RefreshMcpStatus();
        if (HasMcpSnapshot)
            return true;
        NotificationManager?.Show("请先打开 AI 上下文，确认披露范围后刷新 MCP 快照",
            NotificationType.Information);
        return false;
    }

    private void RefreshMcpStatus()
    {
        HasMcpSnapshot = _mcpSetupService.SnapshotExists;
        McpSnapshotStatus = _mcpSetupService.SnapshotStatus;
        _mcpStatusSetting.Status = McpSnapshotStatus;
        _copyAiInstructionsSetting.IsEnabled = HasMcpSnapshot;
        _copyGenericConfigurationSetting.IsEnabled = HasMcpSnapshot;
    }

}
