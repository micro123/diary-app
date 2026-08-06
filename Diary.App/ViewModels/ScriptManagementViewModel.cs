using System.Collections.ObjectModel;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.ViewModels.Dialogs;
using Diary.App.Models;
using Diary.GUIBase.ViewModels;
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
    ScriptCapability Capabilities,
    bool Enabled,
    bool BuildSucceeded,
    string Status,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<ScriptDiagnosticListItem> DiagnosticDetails)
{
    public string ScopeLabel => Scope switch
    {
        ScriptScope.Application => "应用脚本",
        ScriptScope.Editor => "编辑器脚本",
        _ => "未知类型",
    };

    public bool IsLoadFailed => !BuildSucceeded;

    public string CapabilityLabel => Capabilities == ScriptCapability.None
        ? "无额外能力"
        : string.Join("、", Enum.GetValues<ScriptCapability>()
            .Where(capability => capability != ScriptCapability.None && Capabilities.HasFlag(capability))
            .Select(GetCapabilityLabel));

    private static string GetCapabilityLabel(ScriptCapability capability) => capability switch
    {
        ScriptCapability.ReadDiary => "读取日记",
        ScriptCapability.WriteDiary => "写入日记",
        ScriptCapability.UserInteraction => "用户交互",
        ScriptCapability.Clipboard => "剪贴板",
        ScriptCapability.Tracker => "Tracker",
        _ => capability.ToString(),
    };

    public string EnabledLabel => Enabled ? "已启用" : "已禁用";
}

public sealed record ScriptHistoryListItem(
    string ScriptId,
    string ScriptName,
    string Status,
    string Source,
    string StartedAt,
    string Duration,
    IReadOnlyList<string> Diagnostics)
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
    IScriptDirectoryLoader directoryLoader,
    IScriptManager scriptManager,
    IScriptCatalog scriptCatalog,
    IScriptExecutionHistory executionHistory,
    ScriptStartupDiagnosticsStore startupDiagnostics,
    ILogger logger,
    IServiceProvider services) : ViewModelBase
{
    private readonly string _scriptRoot = Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts");

    public ObservableCollection<ScriptListItem> Scripts { get; } = new();
    public ObservableCollection<ScriptListItem> VisibleScripts { get; } = new();
    public ObservableCollection<ScriptHistoryListItem> History { get; } = new();
    public ObservableCollection<ScriptHistoryListItem> VisibleHistory { get; } = new();
    public ObservableCollection<ScriptDiagnosticListItem> DirectoryDiagnostics { get; } = new();
    public ObservableCollection<ScriptDiagnosticListItem> StartupDiagnostics => startupDiagnostics.Diagnostics;
    public IReadOnlyList<string> ScopeFilters { get; } = ["全部类型", "应用脚本", "编辑器脚本"];
    public IReadOnlyList<string> StatusFilters { get; } = ["全部状态", "已加载", "加载失败"];
    public IReadOnlyList<string> HistoryStatusFilters { get; } = ["全部结果", "成功", "失败", "已取消", "已超时", "已拒绝"];
    public IReadOnlyList<string> HistorySourceFilters { get; } = ["全部来源", "手动执行", "编辑器调用", "启动加载", "自动化调用"];
    public IReadOnlyList<string> ExecutionRanges { get; } =
        ["当前日期", "本周", "本月", "本季度", "本年度", "自定义范围"];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedScriptCommand))]
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
    private string _selectedExecutionRange = "当前日期";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private DateTime? _executionStartDate = DateTime.Today;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private DateTime? _executionEndDate = DateTime.Today;
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
    public bool HasDirectoryDiagnostics => DirectoryDiagnostics.Count > 0;
    public bool HasStartupDiagnostics => StartupDiagnostics.Count > 0;
    public bool ShowEditorRange => SelectedScript?.Scope == ScriptScope.Editor;
    public bool ShowCustomRange => ShowEditorRange && SelectedExecutionRange == "自定义范围";
    public string ExecutionRangeSummary => ShowEditorRange
        ? $"{ExecutionStartDate:yyyy-MM-dd} 至 {ExecutionEndDate:yyyy-MM-dd}"
        : "应用脚本，不限定编辑器日期范围";

    partial void OnLoadingChanged(bool value) => OnPropertyChanged(nameof(CanReload));

    partial void OnIsExecutingChanged(bool value) => OnPropertyChanged(nameof(CanReload));

    partial void OnSearchTextChanged(string value) => RefreshVisibleScripts();

    partial void OnSelectedScopeFilterChanged(string value) => RefreshVisibleScripts();

    partial void OnSelectedStatusFilterChanged(string value) => RefreshVisibleScripts();

    partial void OnSelectedHistoryStatusFilterChanged(string value) => RefreshVisibleHistory();

    partial void OnSelectedHistorySourceFilterChanged(string value) => RefreshVisibleHistory();

    partial void OnSelectedScriptChanged(ScriptListItem? value)
    {
        OnPropertyChanged(nameof(HasSelectedDiagnostics));
        OnPropertyChanged(nameof(ShowEditorRange));
        OnPropertyChanged(nameof(ShowCustomRange));
        OnPropertyChanged(nameof(ExecutionRangeSummary));
        RunCommand.NotifyCanExecuteChanged();
        OpenSelectedScriptCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedExecutionRangeChanged(string value)
    {
        if (value != "自定义范围")
        {
            var today = DateTime.Today;
            (ExecutionStartDate, ExecutionEndDate) = value switch
            {
                "本周" => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
                    today.AddDays(6 - ((int)today.DayOfWeek + 6) % 7)),
                "本月" => (new DateTime(today.Year, today.Month, 1), new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month))),
                "本季度" => (new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1),
                    new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 3, DateTime.DaysInMonth(today.Year, (today.Month - 1) / 3 * 3 + 3))),
                _ => (today, today),
            };
        }
        OnPropertyChanged(nameof(ShowCustomRange));
        OnPropertyChanged(nameof(ExecutionRangeSummary));
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnExecutionStartDateChanged(DateTime? value)
    {
        if (SelectedExecutionRange == "当前日期")
            ExecutionEndDate = value;
        OnPropertyChanged(nameof(ExecutionRangeSummary));
    }

    partial void OnExecutionEndDateChanged(DateTime? value) => OnPropertyChanged(nameof(ExecutionRangeSummary));

    public override void OnShow()
    {
        _ = ReloadAsync();
    }

    [RelayCommand]
    private Task Reload() => ReloadAsync();

    private async Task ReloadAsync()
    {
        if (Loading)
            return;
        Loading = true;
        try
        {
            var result = await directoryLoader.LoadAsync(_scriptRoot);
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
                    descriptor?.Capabilities ?? ScriptCapability.None,
                    entry.Enabled,
                    entry.BuildResult?.Succeeded == true,
                    FormatStatus(entry),
                    FormatDiagnostics(entry.BuildResult?.Diagnostics),
                    FormatDiagnosticDetails(entry.BuildResult?.Diagnostics)));
            }
            Scripts.Clear();
            foreach (var script in loadedScripts)
                Scripts.Add(script);
            RefreshVisibleScripts();
            SelectedScript = Scripts.FirstOrDefault(script => script.Id == selectedId);
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
        && SelectedScript is { Enabled: true, BuildSucceeded: true }
        && (SelectedScript.Scope == ScriptScope.Application
            || ExecutionStartDate is not null && ExecutionEndDate is not null && ExecutionStartDate <= ExecutionEndDate);

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
        return entry.Enabled ? "加载失败" : "已禁用";
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
            await ReloadAsync();
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
            ProcUtils.OpenFileCrossPlatform(SelectedScript.SourcePath);
            Status = $"已打开脚本：{SelectedScript.Name}";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "打开脚本文件失败：{SourcePath}", SelectedScript.SourcePath);
            Status = "无法打开脚本文件";
        }
    }

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
                    || SelectedStatusFilter == "已加载" && !script.IsLoadFailed))
            .ToArray();
        VisibleScripts.Clear();
        foreach (var script in visible)
            VisibleScripts.Add(script);
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
                FormatDiagnostics(entry.Outcome.Result.Diagnostics)));
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
        if (script.Scope == ScriptScope.Application)
            return new ScriptExecutionRequest(
                new ScriptTarget(ScriptScope.Application),
                Source: ScriptExecutionSource.Manual);

        var startDate = ExecutionStartDate!.Value;
        var endDate = ExecutionEndDate!.Value;
        return new ScriptExecutionRequest(
            new ScriptTarget(
                ScriptScope.Editor,
                new EditorScriptContext(
                    startDate.ToString("yyyy-MM-dd"),
                    endDate.ToString("yyyy-MM-dd"),
                    GetGranularity(startDate, endDate))),
            Source: ScriptExecutionSource.Manual);
    }

    private static ScriptTimeGranularity GetGranularity(DateTime startDate, DateTime endDate)
    {
        if (startDate.Date == endDate.Date)
            return ScriptTimeGranularity.Day;
        if (startDate.Day == 1 && endDate.Year == startDate.Year && endDate.Month == startDate.Month
            && endDate.Day == DateTime.DaysInMonth(endDate.Year, endDate.Month))
            return ScriptTimeGranularity.Month;
        if (startDate.Month == 1 && startDate.Day == 1 && endDate.Month == 12 && endDate.Day == 31
            && startDate.Year == endDate.Year)
            return ScriptTimeGranularity.Year;
        return ScriptTimeGranularity.Custom;
    }
}
