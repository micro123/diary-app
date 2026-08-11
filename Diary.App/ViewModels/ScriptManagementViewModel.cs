using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.ViewModels.Dialogs;
using Diary.App.Views;
using Diary.App.Models;
using Diary.GUIBase.ViewModels;
using Diary.GUIBase.Utils;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ursa.Controls;

namespace Diary.App.ViewModels;

public sealed record ScriptListItem(
    string SourcePath,
    string Id,
    string Name,
    ScriptScope Scope,
    bool Enabled,
    bool BuildSucceeded,
    string Status,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<ScriptDiagnosticListItem> DiagnosticDetails,
    string Description = "")
{
    public string Language => Path.GetExtension(SourcePath).ToLowerInvariant() switch
    {
        ".cs" => "C#",
        ".lua" => "Lua",
        ".py" => "Python",
        _ => "未知语言",
    };

    public string LanguageIcon => Language switch
    {
        "C#" => "mdi-language-csharp",
        "Lua" => "mdi-language-lua",
        "Python" => "mdi-language-python",
        _ => "mdi-file-code-outline",
    };

    public bool IsCSharp => Language == "C#";

    public bool IsPython => Language == "Python";

    public bool IsLua => Language == "Lua";

    public bool IsSvgLanguage => IsCSharp || IsPython || IsLua;

    public string ScopeLabel => Scope switch
    {
        ScriptScope.Application => "应用脚本",
        ScriptScope.Editor => "编辑器脚本",
        _ => "未知类型",
    };

    public bool IsLoadFailed => !BuildSucceeded;

    public bool IsRunnable => BuildSucceeded && Scope == ScriptScope.Application;

    public string CapabilityLabel => "宿主 API 默认可用";

}

public sealed record ScriptHistoryListItem(
    string ScriptId,
    string ScriptName,
    string Status,
    string Source,
    string StartedAt,
    string Duration,
    IReadOnlyList<string> Diagnostics,
    string Log)
{
    public string StatusLabel => Status switch
    {
        nameof(ScriptExecutionStatus.Succeeded) => "成功",
        nameof(ScriptExecutionStatus.Cancelled) => "已取消",
        nameof(ScriptExecutionStatus.TimedOut) => "已超时",
        nameof(ScriptExecutionStatus.Rejected) => "已拒绝",
        _ => "失败",
    };

    public string SourceLabel => Source switch
    {
        nameof(ScriptExecutionSource.Manual) => "手动执行",
        nameof(ScriptExecutionSource.Editor) => "编辑器调用",
        nameof(ScriptExecutionSource.Startup) => "启动加载",
        nameof(ScriptExecutionSource.Automation) => "自动化调用",
        _ => "未知来源",
    };
}

public sealed record ScriptDiagnosticListItem(
    string SeverityLabel,
    string Code,
    string Message,
    string Location)
{
    public string Summary => string.IsNullOrWhiteSpace(Location)
        ? $"[{Code}] {Message}"
        : $"[{Code}] {Location} {Message}";
}

[DiAutoRegister(singleton: true)]
public partial class ScriptManagementViewModel(
    ScriptDirectoryLoadState directoryLoadState,
    IScriptManager scriptManager,
    IScriptCatalog scriptCatalog,
    IScriptExecutionHistory executionHistory,
    ScriptStartupDiagnosticsStore startupDiagnostics,
    ScriptLogStore scriptLogStore,
    ILogger logger,
    IServiceProvider services) : ViewModelBase
{
    private readonly string _scriptRoot = Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts");

    public ScriptApiReferenceViewModel ApiReference { get; } = new();
    public ObservableCollection<ScriptListItem> Scripts { get; } = new();
    public ObservableCollection<ScriptListItem> VisibleScripts { get; } = new();
    public ObservableCollection<ScriptHistoryListItem> History { get; } = new();
    public ObservableCollection<ScriptHistoryListItem> VisibleHistory { get; } = new();
    public ObservableCollection<ScriptLogEntry> ScriptLogs { get; } = new();
    public ObservableCollection<ScriptDiagnosticListItem> DirectoryDiagnostics { get; } = new();
    public ObservableCollection<ScriptDiagnosticListItem> StartupDiagnostics => startupDiagnostics.Diagnostics;
    public IReadOnlyList<string> ScopeFilters { get; } = ["全部类型", "应用脚本", "编辑器脚本"];
    public IReadOnlyList<string> StatusFilters { get; } = ["全部状态", "已加载", "加载失败"];
    public IReadOnlyList<string> HistoryStatusFilters { get; } = ["全部结果", "成功", "失败", "已取消", "已超时", "已拒绝"];
    public IReadOnlyList<string> HistorySourceFilters { get; } = ["全部来源", "手动执行", "编辑器调用", "启动加载", "自动化调用"];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedScriptCommand))]
    [NotifyPropertyChangedFor(nameof(CanOpenSelectedScript))]
    private ScriptListItem? _selectedScript;

    [ObservableProperty] private string _status = "尚未加载脚本目录";
    [ObservableProperty] private bool _loading;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedScopeFilter = "全部类型";
    [ObservableProperty] private string _selectedStatusFilter = "全部状态";
    [ObservableProperty] private string _selectedHistoryStatusFilter = "全部结果";
    [ObservableProperty] private string _selectedHistorySourceFilter = "全部来源";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isExecuting;
    private CancellationTokenSource? _executionCancellation;

    public bool CanReload => !Loading && !IsExecuting;
    public bool CanOpenSelectedScript => SelectedScript is not null;
    public bool HasScripts => Scripts.Count > 0;
    public bool HasVisibleScripts => VisibleScripts.Count > 0;
    public bool ShowEmptyState => !Loading && !HasScripts;
    public bool ShowNoResultsState => !Loading && HasScripts && !HasVisibleScripts;
    public bool HasSelectedDiagnostics => SelectedScript?.Diagnostics.Count > 0;
    public bool HasScriptLogs => ScriptLogs.Count > 0;
    public bool HasDirectoryDiagnostics => DirectoryDiagnostics.Count > 0;
    public bool HasStartupDiagnostics => StartupDiagnostics.Count > 0;
    private bool _scriptLogSubscribed;
    partial void OnLoadingChanged(bool value) => OnPropertyChanged(nameof(CanReload));

    partial void OnIsExecutingChanged(bool value) => OnPropertyChanged(nameof(CanReload));

    partial void OnSearchTextChanged(string value) => RefreshVisibleScripts();

    partial void OnSelectedScopeFilterChanged(string value) => RefreshVisibleScripts();

    partial void OnSelectedStatusFilterChanged(string value) => RefreshVisibleScripts();

    partial void OnSelectedHistoryStatusFilterChanged(string value) => RefreshVisibleHistory();

    partial void OnSelectedHistorySourceFilterChanged(string value) => RefreshVisibleHistory();

    partial void OnSelectedScriptChanged(ScriptListItem? value)
    {
        if (value is not null && ApiReference.Languages.Contains(value.Language))
            ApiReference.SelectedLanguage = value.Language;
        OnPropertyChanged(nameof(HasSelectedDiagnostics));
        RunCommand.NotifyCanExecuteChanged();
        OpenSelectedScriptCommand.NotifyCanExecuteChanged();
    }

    public override void OnShow()
    {
        if (!_scriptLogSubscribed)
        {
            _scriptLogSubscribed = true;
            scriptLogStore.Changed += OnScriptLogsChanged;
        }
        RefreshScriptLogs();
        ObserveBackgroundTask(LoadAsync(), "脚本管理加载");
    }

    private void OnScriptLogsChanged(object? sender, EventArgs e) => RefreshScriptLogs();

    private void RefreshScriptLogs()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshScriptLogs);
            return;
        }

        ScriptLogs.Clear();
        foreach (var entry in scriptLogStore.GetSnapshot())
            ScriptLogs.Add(entry);
        OnPropertyChanged(nameof(HasScriptLogs));
    }

    [RelayCommand]
    private Task Reload() => ReloadAsync(forceReload: true);

    private Task LoadAsync() => ReloadAsync(forceReload: false);

    private async Task ReloadAsync(bool forceReload)
    {
        if (Loading)
            return;
        Loading = true;
        Status = forceReload ? "正在重新加载脚本目录" : "正在加载脚本目录";
        try
        {
            var result = await (forceReload
                ? directoryLoadState.ReloadAsync(_scriptRoot)
                : directoryLoadState.EnsureLoadedAsync(_scriptRoot));
            DirectoryDiagnostics.Clear();
            foreach (var diagnostic in result.Diagnostics)
                DirectoryDiagnostics.Add(FormatDiagnostic(diagnostic));
            OnPropertyChanged(nameof(HasDirectoryDiagnostics));
            var selectedId = SelectedScript?.Id;
            var loadedScripts = new List<ScriptListItem>();
            foreach (var entry in result.Entries)
            {
                var descriptor = entry.BuildResult?.Program?.Descriptor;
                loadedScripts.Add(new ScriptListItem(
                    entry.SourcePath,
                    descriptor?.Id ?? Path.GetFileNameWithoutExtension(entry.SourcePath),
                    descriptor?.Name ?? Path.GetFileName(entry.SourcePath),
                    entry.Scope,
                    entry.Enabled,
                    entry.BuildResult?.Succeeded == true,
                    FormatStatus(entry),
                    FormatDiagnostics(entry.BuildResult?.Diagnostics),
                     FormatDiagnosticDetails(entry.BuildResult?.Diagnostics),
                     descriptor?.Description ?? string.Empty));
            }
            Scripts.Clear();
            foreach (var script in loadedScripts)
                Scripts.Add(script);
            RefreshVisibleScripts();
            SelectedScript = VisibleScripts.FirstOrDefault(script => script.Id == selectedId)
                ?? VisibleScripts.FirstOrDefault();
            OnPropertyChanged(nameof(HasScripts));
            OnPropertyChanged(nameof(ShowEmptyState));
            RefreshHistory();
            Status = result.Diagnostics.Count(item => item.Severity == ScriptDiagnosticSeverity.Error) is var errors && errors > 0
                ? $"发现 {Scripts.Count} 个脚本，{errors} 个错误"
                : Scripts.Count == 0 ? "尚未发现脚本" : $"已加载 {Scripts.Count} 个脚本";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "重新加载脚本目录失败");
            Status = "脚本目录加载失败";
            DirectoryDiagnostics.Clear();
            DirectoryDiagnostics.Add(new ScriptDiagnosticListItem(
                "错误",
                "SCRIPT_DIRECTORY_LOAD_FAILED",
                "脚本目录加载失败，请查看日志或重试。",
                string.Empty));
            OnPropertyChanged(nameof(HasDirectoryDiagnostics));
        }
        finally
        {
            Loading = false;
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(ShowNoResultsState));
        }
    }

    private bool CanRun() =>
        !IsExecuting
        && SelectedScript is { BuildSucceeded: true }
        && SelectedScript.Scope == ScriptScope.Application;

    private bool CanCancel() => IsExecuting;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        var script = SelectedScript;
        if (script is null)
            return;
        IsExecuting = true;
        Status = $"正在运行 {script.Name}";
        using var cancellation = new CancellationTokenSource();
        _executionCancellation = cancellation;
        try
        {
            var outcome = await Task.Run(async () => await scriptManager.ExecuteAsync(
                script.Id,
                CreateExecutionRequest(script),
                TimeSpan.FromMinutes(5),
                cancellation.Token), cancellation.Token);
            Status = FormatExecutionStatus(script.Name, outcome.Result.Status, outcome.Result.Diagnostics, outcome.Duration);
            NotificationManager?.Show(
                Status,
                outcome.Result.Status == ScriptExecutionStatus.Succeeded
                    ? NotificationType.Success
                    : outcome.Result.Status == ScriptExecutionStatus.Cancelled
                        ? NotificationType.Information
                        : NotificationType.Error);
            RefreshHistory();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "运行脚本失败：{ScriptId}", script.Id);
            Status = $"{script.Name} 执行失败，请查看日志";
            NotificationManager?.Show(Status, NotificationType.Error);
        }
        finally
        {
            _executionCancellation = null;
            IsExecuting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_executionCancellation is null)
            return;
        Status = "正在取消脚本...";
        _executionCancellation.Cancel();
    }

    private static string FormatStatus(ScriptDirectoryEntry entry)
    {
        if (entry.BuildResult is null)
            return "未加载";
        if (entry.BuildResult.Succeeded)
            return entry.BuildResult.Diagnostics.Any(item => item.Code == "SCRIPT_CACHE_HIT")
                ? "已加载（缓存）"
                : "已加载";
        return "加载失败";
    }

    [RelayCommand]
    private async Task CopySelectedDiagnostics()
    {
        var script = SelectedScript;
        if (script is null || script.DiagnosticDetails.Count == 0)
            return;
        if (await CopyStringToClipboardAsync(string.Join(Environment.NewLine, script.DiagnosticDetails.Select(item => item.Summary))))
            NotificationManager?.Show("诊断信息已复制", NotificationType.Success);
    }

    [RelayCommand]
    private async Task CopyDirectoryDiagnostics()
    {
        if (DirectoryDiagnostics.Count == 0)
            return;
        if (await CopyStringToClipboardAsync(string.Join(Environment.NewLine, DirectoryDiagnostics.Select(item => item.Summary))))
            NotificationManager?.Show("目录诊断已复制", NotificationType.Success);
    }

    [RelayCommand]
    private async Task CopyStartupDiagnostics()
    {
        if (!HasStartupDiagnostics)
            return;
        if (await CopyStringToClipboardAsync(string.Join(Environment.NewLine, StartupDiagnostics.Select(item => item.Summary))))
            NotificationManager?.Show("启动诊断已复制", NotificationType.Success);
    }

    [RelayCommand]
    private async Task CopyHistoryLog(ScriptHistoryListItem? history)
    {
        if (history is null)
            return;
        if (await CopyStringToClipboardAsync(history.Log))
            NotificationManager?.Show("执行日志已复制", NotificationType.Success);
    }

    [RelayCommand]
    private Task ClearScriptLogs()
    {
        scriptLogStore.Clear();
        Status = "脚本运行日志已清空";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task ClearExecutionHistory()
    {
        try
        {
            executionHistory.Clear();
            RefreshHistory();
            Status = "执行历史已清空";
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "清空脚本执行历史失败");
        }
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CreateScript()
    {
        var viewModel = services.GetRequiredService<ScriptCreationViewModel>();
        var options = new OverlayDialogOptions
        {
            CanDragMove = false,
            CanResize = false,
            CanLightDismiss = false,
            IsCloseButtonVisible = false,
        };
        var sourcePath = await OverlayDialog.ShowCustomModal<string>(viewModel, options: options);
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            Status = "脚本已创建，正在重新加载";
            await ReloadAsync(forceReload: true);
            SelectedScript = Scripts.FirstOrDefault(script =>
                string.Equals(Path.GetFullPath(script.SourcePath), Path.GetFullPath(sourcePath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            Status = SelectedScript is null ? "脚本已创建并完成检查" : $"脚本已创建并完成检查：{SelectedScript.Name}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedScript))]
    private void OpenSelectedScript()
    {
        if (SelectedScript is null)
            return;
        try
        {
            OpenScriptEditor(SelectedScript.SourcePath);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "打开脚本文件失败：{SourcePath}", SelectedScript.SourcePath);
            Status = "无法打开脚本文件";
        }
    }

    private void OpenScriptEditor(string sourcePath)
    {
        var viewModel = services.GetRequiredService<ScriptEditorViewModel>();
        viewModel.Initialize(sourcePath);
        viewModel.Saved += OnScriptEditorSaved;
        var window = new ScriptEditorWindow(viewModel);
        window.Closed += (_, _) => viewModel.Saved -= OnScriptEditorSaved;
        var owner = TopLevel.GetTopLevel(View) as Window;
        if (owner is not null)
            ObserveBackgroundTask(window.ShowDialog(owner), "打开脚本编辑器");
        else
            window.Show();
        Status = $"已打开脚本编辑器：{Path.GetFileName(sourcePath)}";
    }

    private void OnScriptEditorSaved(object? sender, EventArgs e) =>
        ObserveBackgroundTask(ReloadAsync(forceReload: true), "脚本目录刷新");

    private void ObserveBackgroundTask(Task task, string operation)
    {
        _ = task.ContinueWith(
            completedTask => logger.LogError(completedTask.Exception, "{Operation}失败", operation),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    [RelayCommand]
    private void OpenScript(ScriptListItem? script)
    {
        if (script is null)
            return;
        SelectedScript = script;
        OpenSelectedScript();
    }

    [RelayCommand]
    private void OpenScriptDirectory(ScriptListItem? script)
    {
        if (script is null)
            return;
        try
        {
            ProcUtils.OpenDirectoryCrossPlatform(Path.GetDirectoryName(script.SourcePath) ?? _scriptRoot);
            Status = $"已打开脚本目录：{script.Name}";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "打开脚本目录失败：{SourcePath}", script.SourcePath);
            Status = "无法打开脚本目录";
        }
    }

    [RelayCommand]
    private async Task RunScript(ScriptListItem? script)
    {
        if (script is null)
            return;
        SelectedScript = script;
        if (CanRun())
            await Run();
        else
            Status = $"脚本不可运行：{script.Name}";
    }

    [RelayCommand]
    private async Task RecheckScript(ScriptListItem? script)
    {
        if (script is null || Loading || IsExecuting)
            return;
        SelectedScript = script;
        await ReloadAsync(forceReload: true);
    }

    [RelayCommand]
    private async Task DeleteScript(ScriptListItem? script)
    {
        if (script is null || !IsPathInsideScriptRoot(script.SourcePath))
            return;
        if (!await EventDispatcher.Confirm("删除脚本", $"确认删除“{script.Name}”吗？源码和 metadata 将被永久删除。"))
        {
            Status = "已取消删除脚本";
            return;
        }
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(script.SourcePath));
            var packagePath = directory is not null
                && !string.Equals(directory, Path.GetFullPath(_scriptRoot), GetPathComparison())
                && File.Exists(Path.Combine(directory, "manifest.json"))
                ? directory
                : null;
            if (packagePath is not null)
                Directory.Delete(packagePath, true);
            else
            {
                File.Delete(script.SourcePath);
                var metadataPath = script.SourcePath + ".json";
                if (File.Exists(metadataPath))
                    File.Delete(metadataPath);
            }
            await ReloadAsync(forceReload: true);
            Status = $"脚本已删除：{script.Name}";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "删除脚本失败：{SourcePath}", script.SourcePath);
            Status = "删除脚本失败";
        }
    }

    private bool IsPathInsideScriptRoot(string path) =>
        ScriptCreationPolicy.IsInsideDirectory(Path.GetFullPath(path), Path.GetFullPath(_scriptRoot));

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [RelayCommand]
    private void OpenScriptsFolder()
    {
        try
        {
            Directory.CreateDirectory(_scriptRoot);
            ProcUtils.OpenDirectoryCrossPlatform(_scriptRoot);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "打开脚本目录失败：{ScriptRoot}", _scriptRoot);
            Status = "无法打开脚本目录";
        }
    }

    private void RefreshVisibleScripts()
    {
        var search = SearchText.Trim();
        var visible = Scripts.Where(script =>
                (string.IsNullOrEmpty(search)
                    || script.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || script.Id.Contains(search, StringComparison.OrdinalIgnoreCase))
                && (SelectedScopeFilter == "全部类型" || script.ScopeLabel == SelectedScopeFilter)
                && (SelectedStatusFilter == "全部状态"
                    || SelectedStatusFilter == "加载失败" && script.IsLoadFailed
                    || SelectedStatusFilter == "已加载" && script.BuildSucceeded))
            .ToArray();
        VisibleScripts.Clear();
        foreach (var script in visible)
            VisibleScripts.Add(script);
        if (SelectedScript is not null && !VisibleScripts.Contains(SelectedScript))
            SelectedScript = VisibleScripts.FirstOrDefault();
        OnPropertyChanged(nameof(HasVisibleScripts));
        OnPropertyChanged(nameof(ShowNoResultsState));
    }

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var entry in executionHistory.GetRecent())
        {
            var scriptName = scriptCatalog.TryGet(entry.ScriptId, out var program) && program is not null
                ? program.Descriptor.Name
                : entry.ScriptId;
            History.Add(new ScriptHistoryListItem(
                entry.ScriptId,
                scriptName,
                entry.Outcome.Result.Status.ToString(),
                entry.Outcome.Source.ToString(),
                (entry.Outcome.StartedAt ?? entry.RecordedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                $"{entry.Outcome.Duration.TotalMilliseconds:0} ms",
                FormatDiagnostics(entry.Outcome.Result.Diagnostics),
                FormatHistoryLog(entry, scriptName)));
        }
        RefreshVisibleHistory();
    }

    private void RefreshVisibleHistory()
    {
        var visible = History.Where(entry =>
                (SelectedHistoryStatusFilter == "全部结果" || entry.StatusLabel == SelectedHistoryStatusFilter)
                && (SelectedHistorySourceFilter == "全部来源" || entry.SourceLabel == SelectedHistorySourceFilter))
            .ToArray();
        VisibleHistory.Clear();
        foreach (var entry in visible)
            VisibleHistory.Add(entry);
    }

    private static IReadOnlyList<string> FormatDiagnostics(IEnumerable<ScriptDiagnostic>? diagnostics) =>
        diagnostics?.Select(item => item.SourcePath is null
                ? $"[{item.Code}] {item.Message}"
                : $"[{item.Code}] {item.SourcePath}:{item.Line}:{item.Column} {item.Message}")
            .ToArray()
            ?? Array.Empty<string>();

    private static string FormatHistoryLog(ScriptExecutionHistoryEntry entry, string scriptName)
    {
        var outcome = entry.Outcome;
        var lines = new List<string>
        {
            $"脚本：{scriptName} ({entry.ScriptId})",
            $"状态：{outcome.Result.Status}",
            $"来源：{outcome.Source}",
            $"开始时间：{(outcome.StartedAt ?? entry.RecordedAt).ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            $"耗时：{outcome.Duration.TotalMilliseconds:0} ms",
            $"执行 ID：{outcome.ExecutionId}",
        };
        if (!string.IsNullOrWhiteSpace(outcome.WorkerId))
            lines.Add($"Worker ID：{outcome.WorkerId}");
        if (!string.IsNullOrWhiteSpace(outcome.WorkerRequestId))
            lines.Add($"Worker 请求 ID：{outcome.WorkerRequestId}");
        lines.Add("诊断：");
        lines.AddRange(FormatDiagnostics(outcome.Result.Diagnostics));
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatExecutionStatus(
        string scriptName,
        ScriptExecutionStatus status,
        IReadOnlyList<ScriptDiagnostic> diagnostics,
        TimeSpan duration) => status switch
        {
            ScriptExecutionStatus.Succeeded => $"{scriptName} 执行成功（{duration.TotalSeconds:0.##} 秒）",
            ScriptExecutionStatus.Cancelled => $"{scriptName} 已取消",
            ScriptExecutionStatus.TimedOut => $"{scriptName} 执行超时，请查看诊断详情",
            ScriptExecutionStatus.Rejected => $"{scriptName} 未执行：{diagnostics.FirstOrDefault()?.Message ?? "请求被拒绝"}",
            _ => $"{scriptName} 执行失败：{diagnostics.FirstOrDefault()?.Message ?? "请查看诊断详情"}",
        };

    private static IReadOnlyList<ScriptDiagnosticListItem> FormatDiagnosticDetails(
        IEnumerable<ScriptDiagnostic>? diagnostics) =>
        diagnostics?.Select(FormatDiagnostic).ToArray() ?? Array.Empty<ScriptDiagnosticListItem>();

    private static ScriptDiagnosticListItem FormatDiagnostic(ScriptDiagnostic diagnostic) =>
        new(
            diagnostic.Severity switch
            {
                ScriptDiagnosticSeverity.Error => "错误",
                ScriptDiagnosticSeverity.Warning => "警告",
                _ => "信息",
            },
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.SourcePath is null
                ? string.Empty
                : $"{diagnostic.SourcePath}:{diagnostic.Line}:{diagnostic.Column}");

    private ScriptExecutionRequest CreateExecutionRequest(ScriptListItem script)
    {
        return new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual);
    }
}
