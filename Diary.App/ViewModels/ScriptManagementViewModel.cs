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
    string Status);

[DiAutoRegister(singleton: true)]
public partial class ScriptManagementViewModel(
    IScriptDirectoryLoader directoryLoader,
    IScriptManager scriptManager,
    ILogger logger) : ViewModelBase
{
    private readonly string _scriptRoot = Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts");

    public ObservableCollection<ScriptListItem> Scripts { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleEnabledCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private ScriptListItem? _selectedScript;

    [ObservableProperty] private string _status = "尚未加载脚本目录";
    [ObservableProperty] private bool _loading;

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
            Scripts.Clear();
            foreach (var entry in result.Entries)
            {
                var descriptor = entry.BuildResult?.Program?.Descriptor;
                Scripts.Add(new ScriptListItem(
                    entry.SourcePath,
                    descriptor?.Id ?? Path.GetFileNameWithoutExtension(entry.SourcePath),
                    descriptor?.Name ?? Path.GetFileName(entry.SourcePath),
                    entry.Scope,
                    descriptor?.Capabilities ?? ScriptCapability.None,
                    entry.Enabled,
                    entry.BuildResult?.Succeeded == true,
                    FormatStatus(entry)));
            }
            Status = $"发现 {Scripts.Count} 个脚本，{result.Diagnostics.Count(item => item.Severity == ScriptDiagnosticSeverity.Error)} 个错误";
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

    private bool CanToggleEnabled() => SelectedScript is not null;

    [RelayCommand(CanExecute = nameof(CanToggleEnabled))]
    private async Task ToggleEnabled()
    {
        var script = SelectedScript;
        if (script is null)
            return;
        await directoryLoader.SetEnabledAsync(script.SourcePath, !script.Enabled);
        await ReloadAsync();
    }

    private bool CanRun() =>
        SelectedScript is { Enabled: true, BuildSucceeded: true, Scope: ScriptScope.Application };

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        var script = SelectedScript;
        if (script is null)
            return;
        var outcome = await Task.Run(async () => await scriptManager.ExecuteAsync(
            script.Id,
            new ScriptExecutionRequest(new ScriptTarget(ScriptScope.Application))));
        Status = outcome.Result.Status == ScriptExecutionStatus.Succeeded
            ? $"{script.Name} 执行成功"
            : $"{script.Name} 执行失败：{outcome.Result.Diagnostics.FirstOrDefault()?.Message}";
        NotificationManager?.Show(
            Status,
            outcome.Result.Status == ScriptExecutionStatus.Succeeded
                ? NotificationType.Success
                : NotificationType.Error);
    }

    private static string FormatStatus(ScriptDirectoryEntry entry)
    {
        if (!entry.Enabled)
            return "已禁用";
        if (entry.BuildResult is null)
            return "未构建";
        if (entry.BuildResult.Succeeded)
            return entry.BuildResult.Diagnostics.Any(item => item.Code == "SCRIPT_CACHE_HIT")
                ? "已加载（缓存）"
                : "已加载";
        return entry.BuildResult.Diagnostics.FirstOrDefault()?.Message ?? "构建失败";
    }
}
