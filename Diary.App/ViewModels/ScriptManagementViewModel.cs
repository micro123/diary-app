using System.Collections.ObjectModel;
using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.Utils;
using Microsoft.Extensions.Logging;

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
    IReadOnlyList<string> Diagnostics)
{
    public string ScopeLabel => Scope switch
    {
        ScriptScope.Application => "应用脚本",
        ScriptScope.Editor => "编辑器脚本",
        _ => "未知类型",
    };

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
}

public sealed record ScriptHistoryListItem(
    string ScriptId,
    string Status,
    string Source,
    string StartedAt,
    string Duration,
    IReadOnlyList<string> Diagnostics);

[DiAutoRegister(singleton: true)]
public partial class ScriptManagementViewModel(
    IScriptDirectoryLoader directoryLoader,
    IScriptManager scriptManager,
    IScriptExecutionHistory executionHistory,
    ILogger logger) : ViewModelBase
{
    private readonly string _scriptRoot = Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts");

    public ObservableCollection<ScriptListItem> Scripts { get; } = new();
    public ObservableCollection<ScriptHistoryListItem> History { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private ScriptListItem? _selectedScript;

    [ObservableProperty] private string _status = "尚未加载脚本目录";
    [ObservableProperty] private bool _loading;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isExecuting;
    private CancellationTokenSource? _executionCancellation;

    public bool CanReload => !Loading && !IsExecuting;

    partial void OnLoadingChanged(bool value) => OnPropertyChanged(nameof(CanReload));

    partial void OnIsExecutingChanged(bool value) => OnPropertyChanged(nameof(CanReload));

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
                    FormatDiagnostics(entry.BuildResult?.Diagnostics)));
            }
            Scripts.Clear();
            foreach (var script in loadedScripts)
                Scripts.Add(script);
            SelectedScript = Scripts.FirstOrDefault(script => script.Id == selectedId);
            RefreshHistory();
            Status = result.Diagnostics.Count(item => item.Severity == ScriptDiagnosticSeverity.Error) is var errors && errors > 0
                ? $"发现 {Scripts.Count} 个脚本，{errors} 个错误"
                : Scripts.Count == 0 ? "尚未发现脚本" : $"已加载 {Scripts.Count} 个脚本";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "重新加载脚本目录失败");
            Status = "脚本目录加载失败";
        }
        finally
        {
            Loading = false;
        }
    }

    private bool CanRun() =>
        !IsExecuting && SelectedScript is { Enabled: true, BuildSucceeded: true, Scope: ScriptScope.Application };

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
                new ScriptExecutionRequest(
                    new ScriptTarget(ScriptScope.Application),
                    Source: ScriptExecutionSource.Manual),
                TimeSpan.FromMinutes(5),
                cancellation.Token), cancellation.Token);
            Status = outcome.Result.Status switch
            {
                ScriptExecutionStatus.Succeeded => $"{script.Name} 执行成功（{outcome.Duration.TotalSeconds:0.##} 秒）",
                ScriptExecutionStatus.Cancelled => $"{script.Name} 已取消",
                ScriptExecutionStatus.TimedOut => $"{script.Name} 执行超时",
                _ => $"{script.Name} 执行失败：{outcome.Result.Diagnostics.FirstOrDefault()?.Message ?? "请查看诊断详情"}",
            };
            NotificationManager?.Show(
                Status,
                outcome.Result.Status == ScriptExecutionStatus.Succeeded
                    ? NotificationType.Success
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
        return "加载失败（已禁用）";
    }

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var entry in executionHistory.GetRecent())
        {
            History.Add(new ScriptHistoryListItem(
                entry.ScriptId,
                entry.Outcome.Result.Status.ToString(),
                entry.Outcome.Source.ToString(),
                (entry.Outcome.StartedAt ?? entry.RecordedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                $"{entry.Outcome.Duration.TotalMilliseconds:0} ms",
                FormatDiagnostics(entry.Outcome.Result.Diagnostics)));
        }
    }

    private static IReadOnlyList<string> FormatDiagnostics(IEnumerable<ScriptDiagnostic>? diagnostics) =>
        diagnostics?.Select(item => item.SourcePath is null
                ? $"[{item.Code}] {item.Message}"
                : $"[{item.Code}] {item.SourcePath}:{item.Line}:{item.Column} {item.Message}")
            .ToArray()
        ?? Array.Empty<string>();
}
