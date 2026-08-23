using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.AiContext;
using Diary.App.Services;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels;

[DiAutoRegister(singleton: true)]
public partial class AiScriptContextViewModel(
    AiContextSnapshotService snapshotService,
    ILogger<AiScriptContextViewModel> logger) : ViewModelBase
{
    private AiContextSnapshot? _snapshot;

    [ObservableProperty] private bool _includeTags = true;
    [ObservableProperty] private bool _includeExtraFieldDefinitions = true;
    [ObservableProperty] private bool _includeTemplates = true;
    [ObservableProperty] private bool _includeTrackerInstances = true;
    [ObservableProperty] private bool _includeSavedQueries = true;
    [ObservableProperty] private bool _includeHostCapabilities = true;
    [ObservableProperty] private bool _includeWorkItems;
    [ObservableProperty] private string _startDate = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd");
    [ObservableProperty] private string _endDate = DateTime.Today.ToString("yyyy-MM-dd");
    [ObservableProperty] private int _maxWorkItems = 50;
    [ObservableProperty] private string _preview = "点击“生成预览”查看本次将披露的数据。";
    [ObservableProperty] private string _status = "默认不包含事项正文、备注和附加字段值。";
    [ObservableProperty] private bool _busy;

    public string McpSnapshotPath => snapshotService.DefaultMcpSnapshotPath;
    public string McpExecutablePath => Path.Combine(
        AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Diary.Mcp.exe" : "Diary.Mcp");
    public string McpCommand => $"\"{McpExecutablePath}\" --snapshot \"{McpSnapshotPath}\"";

    [RelayCommand]
    private void GeneratePreview()
    {
        try
        {
            _snapshot = snapshotService.Build(CreateOptions());
            Preview = AiContextSerializer.ToMarkdown(_snapshot);
            Status = FormatStatus("预览已生成", _snapshot);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Status = exception.Message;
            logger.LogWarning(exception, "生成 AI 上下文预览失败");
        }
    }

    [RelayCommand]
    private async Task RefreshMcpSnapshot()
    {
        await RunBusyAsync(async () =>
        {
            _snapshot = snapshotService.Build(CreateOptions());
            await AiContextSerializer.SaveAsync(McpSnapshotPath, _snapshot);
            Preview = AiContextSerializer.ToMarkdown(_snapshot);
            Status = FormatStatus("MCP 快照已刷新", _snapshot) + $"；路径：{McpSnapshotPath}";
        }, "刷新 MCP 快照失败");
    }

    [RelayCommand]
    private Task ExportJson() => ExportAsync("JSON", ".json", async (path, snapshot) =>
        await AiContextSerializer.SaveAsync(path, snapshot));

    [RelayCommand]
    private Task ExportMarkdown() => ExportAsync("Markdown", ".md", async (path, snapshot) =>
        await File.WriteAllTextAsync(path, AiContextSerializer.ToMarkdown(snapshot)));

    [RelayCommand]
    private void OpenGuide()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Docs", "AiScriptContextGuide.md");
        if (!File.Exists(path))
        {
            Status = "安装目录中未找到 AI 上下文使用文档。";
            return;
        }
        ProcUtils.OpenFileCrossPlatform(path);
    }

    private async Task ExportAsync(
        string formatName,
        string extension,
        Func<string, AiContextSnapshot, Task> writer)
    {
        var storageProvider = GetStorageProvider();
        if (storageProvider is null)
        {
            Status = "当前没有可用的文件选择器。";
            return;
        }
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"导出 AI 上下文 {formatName}",
            SuggestedFileName = $"DiaryApp-ai-context-{DateTime.Now:yyyyMMdd-HHmmss}{extension}",
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(formatName) { Patterns = [$"*{extension}"] }],
        });
        if (file is null)
            return;
        await RunBusyAsync(async () =>
        {
            _snapshot = snapshotService.Build(CreateOptions());
            var path = EnsureExtension(file.Path.LocalPath, extension);
            await writer(path, _snapshot);
            Preview = AiContextSerializer.ToMarkdown(_snapshot);
            Status = FormatStatus($"{formatName} 已导出", _snapshot) + $"；路径：{path}";
        }, $"导出 AI 上下文 {formatName} 失败");
    }

    private async Task RunBusyAsync(Func<Task> action, string failureMessage)
    {
        if (Busy)
            return;
        Busy = true;
        try
        {
            await action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException)
        {
            Status = $"{failureMessage}：{exception.Message}";
            logger.LogError(exception, "{FailureMessage}", failureMessage);
        }
        finally
        {
            Busy = false;
        }
    }

    private AiContextBuildOptions CreateOptions() => new(
        IncludeTags,
        IncludeExtraFieldDefinitions,
        IncludeTemplates,
        IncludeTrackerInstances,
        IncludeSavedQueries,
        IncludeHostCapabilities,
        IncludeWorkItems,
        IncludeWorkItems ? StartDate : null,
        IncludeWorkItems ? EndDate : null,
        MaxWorkItems);

    private static string FormatStatus(string prefix, AiContextSnapshot snapshot) =>
        $"{prefix}：标签 {snapshot.Audit.TagCount}，字段 {snapshot.Audit.ExtraFieldDefinitionCount}，模板 {snapshot.Audit.TemplateCount}，Tracker {snapshot.Audit.TrackerInstanceCount}，保存查询 {snapshot.Audit.SavedQueryCount}，事项 {snapshot.Audit.WorkItemCount}";

    private static string EnsureExtension(string path, string extension) =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;

    private static IStorageProvider? GetStorageProvider() =>
        TopLevel.GetTopLevel(App.Instance.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null)?.StorageProvider;
}
