using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.ViewModels.Dialogs;
using Diary.App.Views;
using Diary.App.Models;
using Diary.App.Services;
using Diary.GUIBase.Events;
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
    bool BuildSucceeded,
    string Status,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<ScriptDiagnosticListItem> DiagnosticDetails,
    string Description = "",
    ScriptEntryKind EntryKind = ScriptEntryKind.Application,
    ScriptFileMetadata? Metadata = null,
    ScriptDescriptor? Descriptor = null,
    ScriptConfigurationState ConfigurationState = ScriptConfigurationState.Ready)
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

    public ScriptApiVersion ApiVersion => Descriptor?.ApiVersion
        ?? Metadata?.ApiVersion
        ?? ScriptApiVersion.V1;

    public string ApiVersionLabel => $"V{(int)ApiVersion}";

    public string ApiVersionDescription => $"脚本 API {ApiVersionLabel}";

    public bool IsApiV1 => ApiVersion == ScriptApiVersion.V1;

    public bool IsApiV2OrLater => ApiVersion != ScriptApiVersion.V1;

    public string ScopeLabel => Scope switch
    {
        ScriptScope.Application => "应用脚本",
        ScriptScope.Editor => "编辑器脚本",
        _ => "未知类型",
    };

    public string EntryKindLabel => EntryKind switch
    {
        ScriptEntryKind.Editor => "编辑器入口",
        ScriptEntryKind.Automation => "自动化入口",
        ScriptEntryKind.Query => "查询入口",
        _ => "应用入口",
    };

    public bool IsLoadFailed => !BuildSucceeded;

    public bool IsRunnable => BuildSucceeded
        && ConfigurationState == ScriptConfigurationState.Ready
        && Scope == ScriptScope.Application;

    public bool NeedsConfiguration => ConfigurationState == ScriptConfigurationState.NeedsConfiguration;

    public bool IsAutomation => EntryKind == ScriptEntryKind.Automation;

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
    string Log,
    ScriptEffectSummary? Effects = null)
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
        nameof(ScriptExecutionSource.WorkItemCreated) => "工作项创建",
        nameof(ScriptExecutionSource.WorkItemSaved) => "工作项保存",
        nameof(ScriptExecutionSource.TagAdded) => "标签添加",
        _ => "未知来源",
    };

    public bool HasEffects => Effects is not null;

    public string EffectsSummary => FormatEffects(Effects);

    public static string FormatEffects(ScriptEffectSummary? effects)
    {
        if (effects is null)
            return string.Empty;
        var parts = new List<string>();
        if (effects.Preview)
            parts.Add("预览执行，未写入");
        else if (effects.AppendedCount > 0)
            parts.Add($"新增 {effects.AppendedCount} 条工作记录");
        else if (!string.IsNullOrWhiteSpace(effects.IdempotencyKey))
            parts.Add("幂等重放，未重复追加");
        if (!string.IsNullOrWhiteSpace(effects.IdempotencyKey))
            parts.Add($"幂等键：{effects.IdempotencyKey}");
        if (effects.CreatedWorkItemIds is { Count: > 0 })
            parts.Add($"新建 ID：{string.Join(", ", effects.CreatedWorkItemIds)}");
        if (effects.RemoteEffects is { Count: > 0 })
            parts.Add($"远程效果：{string.Join("; ", effects.RemoteEffects)}");
        return string.Join("；", parts);
    }
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

public sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

[DiAutoRegister(singleton: true)]
public partial class ScriptManagementViewModel(
    ScriptDirectoryLoadState directoryLoadState,
    IScriptManager scriptManager,
    IScriptCatalog scriptCatalog,
    IScriptExecutionHistory executionHistory,
    ScriptLogStore scriptLogStore,
    ScriptProgressTracker progressTracker,
    ScriptAutomationScheduler scheduler,
    ScriptSharePackageService sharePackageService,
    IScriptLastArgumentsStore lastArgumentsStore,
    AiScriptContextViewModel aiContext,
    ILogger logger,
    IServiceProvider services) : ViewModelBase
{
    public override bool IsViewCacheable => true;

    private const int HistoryTabIndex = 3;
    private const int ScriptLogsTabIndex = 4;
    public const int AiContextTabIndex = 5;

    private readonly string _scriptRoot = Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts");

    public AiScriptContextViewModel AiContext { get; } = aiContext;
    public ResettableObservableCollection<ScriptListItem> Scripts { get; } = new();
    public ResettableObservableCollection<ScriptListItem> VisibleScripts { get; } = new();
    public ResettableObservableCollection<ScriptHistoryListItem> History { get; } = new();
    public ResettableObservableCollection<ScriptHistoryListItem> VisibleHistory { get; } = new();
    public ResettableObservableCollection<ScriptLogEntry> ScriptLogs { get; } = new();
    public ResettableObservableCollection<ScriptDiagnosticListItem> DirectoryDiagnostics { get; } = new();
    public IReadOnlyList<string> ScopeFilters { get; } = ["全部类型", "应用脚本", "编辑器脚本"];
    public IReadOnlyList<string> StatusFilters { get; } = ["全部状态", "已加载", "加载失败"];
    public IReadOnlyList<string> HistoryStatusFilters { get; } = ["全部结果", "成功", "失败", "已取消", "已超时", "已拒绝"];
    public IReadOnlyList<string> HistorySourceFilters { get; } = [
        "全部来源", "手动执行", "编辑器调用", "启动加载", "自动化调用",
        "工作项创建", "工作项保存", "标签添加"];
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
    [ObservableProperty] private int _selectedDetailTabIndex;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isExecuting;
    private CancellationTokenSource? _executionCancellation;
    [ObservableProperty] private double _progressFraction;
    [ObservableProperty] private string _progressMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMetadataSettingsCommand))]
    private string _metadataName = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMetadataSettingsCommand))]
    private string _metadataDescription = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMetadataSettingsCommand))]
    private string _automationScheduleText = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMetadataSettingsCommand))]
    private string _defaultArgumentsText = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMetadataSettingsCommand))]
    private ScriptParameterFormViewModel? _defaultParameterForm;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMetadataSettingsCommand))]
    private int _defaultTimeoutSeconds = 300;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveMetadataSettingsCommand))]
    private bool _automationRunOnStartup;
    [ObservableProperty] private bool _automationTriggerOnWorkItemCreated;
    [ObservableProperty] private bool _automationTriggerOnWorkItemSaved;
    [ObservableProperty] private bool _automationTriggerOnTagAdded;
    [ObservableProperty] private string _metadataSettingsError = string.Empty;

    public bool HasProgress => IsExecuting && !string.IsNullOrWhiteSpace(ProgressMessage);
    public bool HasMetadataSettingsError => !string.IsNullOrWhiteSpace(MetadataSettingsError);
    public bool CanEditMetadataIdentity => SelectedScript?.IsCSharp == false;
    public bool UsesTypedDefaultParameters => DefaultParameterForm is not null;
    public bool UsesLegacyDefaultParameters => !UsesTypedDefaultParameters;

    public bool CanSaveMetadataSettings =>
        SelectedScript is { BuildSucceeded: true } && !IsExecuting;

    public bool CanReload => !Loading && !IsExecuting;
    public bool CanExportScripts => !Loading && !IsExecuting && HasExportableScripts;
    public bool CanOpenSelectedScript => SelectedScript is not null;
    public bool HasScripts => Scripts.Count > 0;
    public bool HasExportableScripts => Scripts.Any(script => script.BuildSucceeded);
    public bool HasVisibleScripts => VisibleScripts.Count > 0;
    public bool ShowEmptyState => !Loading && !HasScripts;
    public bool ShowNoResultsState => !Loading && HasScripts && !HasVisibleScripts;
    public bool HasSelectedDiagnostics => SelectedScript?.Diagnostics.Count > 0;
    public bool HasScriptLogs => ScriptLogs.Count > 0;
    public string ScriptLogsText { get; private set; } = string.Empty;
    public bool HasDirectoryDiagnostics => DirectoryDiagnostics.Count > 0;
    private readonly object _initializationSync = new();
    private Task<PreparedScriptDirectoryData>? _initializationTask;
    private bool _dataApplied;
    private bool _scriptLogSubscribed;
    private bool _progressSubscribed;
    private bool _historyLoaded;
    private bool _historyDirty = true;
    private bool _scriptLogsLoaded;
    private bool _scriptLogsDirty = true;
    partial void OnLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanReload));
        ExportScriptsCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsExecutingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanReload));
        OnPropertyChanged(nameof(HasProgress));
        SaveMetadataSettingsCommand.NotifyCanExecuteChanged();
        ExportScriptsCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchTextChanged(string value) => RefreshVisibleScripts();

    partial void OnSelectedScopeFilterChanged(string value) => RefreshVisibleScripts();

    partial void OnSelectedStatusFilterChanged(string value) => RefreshVisibleScripts();

    partial void OnSelectedHistoryStatusFilterChanged(string value) => RefreshVisibleHistory();

    partial void OnSelectedHistorySourceFilterChanged(string value) => RefreshVisibleHistory();

    partial void OnDefaultParameterFormChanged(ScriptParameterFormViewModel? value)
    {
        OnPropertyChanged(nameof(UsesTypedDefaultParameters));
        OnPropertyChanged(nameof(UsesLegacyDefaultParameters));
    }

    partial void OnSelectedDetailTabIndexChanged(int value)
    {
        if (value == HistoryTabIndex)
            EnsureHistoryLoaded();
        else if (value == ScriptLogsTabIndex)
            EnsureScriptLogsLoaded();
    }

    partial void OnSelectedScriptChanged(ScriptListItem? value)
    {
        MetadataSettingsError = string.Empty;
        MetadataName = value?.Metadata?.Name ?? value?.Name ?? string.Empty;
        MetadataDescription = value?.Metadata?.Description ?? value?.Description ?? string.Empty;
        AutomationScheduleText = value?.Metadata?.Schedule ?? string.Empty;
        DefaultArgumentsText = string.Join(
            Environment.NewLine,
            value?.Metadata?.DefaultArguments?
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}")
            ?? []);
        DefaultParameterForm = value?.Descriptor is { ApiVersion: ScriptApiVersion.V2 } descriptor
            ? new ScriptParameterFormViewModel(
                descriptor,
                value.Metadata?.DefaultArguments,
                mode: ScriptParameterFormMode.MetadataDefaults)
            : null;
        DefaultTimeoutSeconds = value?.Metadata?.TimeoutSeconds is > 0 and <= 3600
            ? value.Metadata.TimeoutSeconds.Value
            : 300;
        AutomationRunOnStartup = value?.Metadata?.RunOnStartup ?? false;
        var triggers = value?.Metadata?.Triggers ?? [];
        AutomationTriggerOnWorkItemCreated = triggers.Contains(ScriptAutomationTriggerKind.WorkItemCreated);
        AutomationTriggerOnWorkItemSaved = triggers.Contains(ScriptAutomationTriggerKind.WorkItemSaved);
        AutomationTriggerOnTagAdded = triggers.Contains(ScriptAutomationTriggerKind.TagAdded);
        OnPropertyChanged(nameof(HasMetadataSettingsError));
        OnPropertyChanged(nameof(HasSelectedDiagnostics));
        OnPropertyChanged(nameof(CanEditMetadataIdentity));
        RunCommand.NotifyCanExecuteChanged();
        OpenSelectedScriptCommand.NotifyCanExecuteChanged();
        SaveMetadataSettingsCommand.NotifyCanExecuteChanged();
    }

    public override void OnShow()
    {
        EnsureSubscriptions();
        if (_dataApplied || Loading)
            return;

        Loading = true;
        Status = "正在加载脚本目录";
        Dispatcher.UIThread.Post(
            () => ObserveBackgroundTask(CompleteLoadAsync(GetPreparedInitialDataAsync(), forceReload: false), "脚本管理加载"),
            DispatcherPriority.Background);
    }

    public override async Task PreloadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await GetPreparedInitialDataAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ResetInitialPreparation();
            logger.LogWarning(exception, "脚本管理预加载失败，将在页面打开时重试");
        }
    }

    private Task<PreparedScriptDirectoryData> GetPreparedInitialDataAsync()
    {
        lock (_initializationSync)
            return _initializationTask ??= PrepareDirectoryDataAsync(forceReload: false);
    }

    private void ResetInitialPreparation()
    {
        lock (_initializationSync)
            _initializationTask = null;
    }

    private void EnsureSubscriptions()
    {
        if (!_scriptLogSubscribed)
        {
            _scriptLogSubscribed = true;
            scriptLogStore.Changed += OnScriptLogsChanged;
        }
        if (!_progressSubscribed)
        {
            _progressSubscribed = true;
            progressTracker.Changed += OnProgressChanged;
        }
    }

    private void OnScriptLogsChanged(object? sender, EventArgs e)
    {
        _scriptLogsDirty = true;
        if (SelectedDetailTabIndex == ScriptLogsTabIndex)
            EnsureScriptLogsLoaded();
    }

    private void OnProgressChanged(object? sender, EventArgs e) => RefreshProgress();

    private void RefreshProgress()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshProgress);
            return;
        }
        if (!IsExecuting || progressTracker.LastReported is not { } latest)
            return;
        ProgressFraction = latest.Fraction;
        ProgressMessage = latest.Message;
        OnPropertyChanged(nameof(HasProgress));
    }

    private void EnsureScriptLogsLoaded()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(EnsureScriptLogsLoaded);
            return;
        }
        if (_scriptLogsLoaded && !_scriptLogsDirty)
            return;

        var snapshot = scriptLogStore.GetSnapshot();
        ScriptLogs.ReplaceAll(snapshot);
        ScriptLogsText = ScriptLogStore.FormatText(snapshot);
        _scriptLogsLoaded = true;
        _scriptLogsDirty = false;
        OnPropertyChanged(nameof(HasScriptLogs));
        OnPropertyChanged(nameof(ScriptLogsText));
    }

    [RelayCommand]
    private Task Reload() => ReloadAsync(forceReload: true);

    private async Task ReloadAsync(bool forceReload)
    {
        if (Loading)
            return;
        Loading = true;
        Status = forceReload ? "正在重新加载脚本目录" : "正在加载脚本目录";
        var preparedTask = forceReload
            ? PrepareDirectoryDataAsync(forceReload: true)
            : GetPreparedInitialDataAsync();
        await CompleteLoadAsync(preparedTask, forceReload);
    }

    private async Task<PreparedScriptDirectoryData> PrepareDirectoryDataAsync(bool forceReload)
    {
        var directoryStarted = Stopwatch.GetTimestamp();
        var result = await (forceReload
            ? directoryLoadState.ReloadAsync(_scriptRoot)
            : directoryLoadState.EnsureLoadedAsync(_scriptRoot));
        var directoryElapsed = Stopwatch.GetElapsedTime(directoryStarted);
        var prepareStarted = Stopwatch.GetTimestamp();
        var prepared = await Task.Run(() => PrepareDirectoryResult(result));
        return prepared with
        {
            DirectoryElapsed = directoryElapsed,
            PrepareElapsed = Stopwatch.GetElapsedTime(prepareStarted),
        };
    }

    private async Task CompleteLoadAsync(
        Task<PreparedScriptDirectoryData> preparedTask,
        bool forceReload)
    {
        try
        {
            var prepared = await preparedTask;
            var applyStarted = Stopwatch.GetTimestamp();
            DirectoryDiagnostics.ReplaceAll(prepared.Diagnostics);
            OnPropertyChanged(nameof(HasDirectoryDiagnostics));
            var selectedId = SelectedScript?.Id;
            Scripts.ReplaceAll(prepared.Scripts);
            RefreshVisibleScripts();
            SelectedScript = VisibleScripts.FirstOrDefault(script => script.Id == selectedId)
                ?? VisibleScripts.FirstOrDefault();
            OnPropertyChanged(nameof(HasScripts));
            OnPropertyChanged(nameof(HasExportableScripts));
            OnPropertyChanged(nameof(ShowEmptyState));
            ExportScriptsCommand.NotifyCanExecuteChanged();
            MarkHistoryDirty();
            scheduler.ApplyLoadResult(prepared.Source);
            Status = prepared.ErrorCount > 0
                ? $"发现 {Scripts.Count} 个脚本，{prepared.ErrorCount} 个错误"
                : Scripts.Count == 0 ? "尚未发现脚本" : $"已加载 {Scripts.Count} 个脚本";
            _dataApplied = true;
            if (forceReload)
            {
                lock (_initializationSync)
                    _initializationTask = Task.FromResult(prepared);
            }
            var applyElapsed = Stopwatch.GetElapsedTime(applyStarted);
            logger.LogInformation(
                "脚本管理加载完成：{Mode}，目录等待 {DirectoryMs:F0} ms，后台整理 {PrepareMs:F0} ms，UI 应用 {ApplyMs:F0} ms，总计 {TotalMs:F0} ms，脚本 {ScriptCount} 个，诊断 {DiagnosticCount} 条",
                forceReload ? "强制刷新" : "首次加载",
                prepared.DirectoryElapsed.TotalMilliseconds,
                prepared.PrepareElapsed.TotalMilliseconds,
                applyElapsed.TotalMilliseconds,
                (prepared.DirectoryElapsed + prepared.PrepareElapsed + applyElapsed).TotalMilliseconds,
                prepared.Scripts.Count,
                prepared.Diagnostics.Count);
        }
        catch (Exception exception)
        {
            if (!forceReload)
                ResetInitialPreparation();
            logger.LogError(exception, "重新加载脚本目录失败");
            Status = "脚本目录加载失败";
            DirectoryDiagnostics.ReplaceAll([new ScriptDiagnosticListItem(
                "错误",
                "SCRIPT_DIRECTORY_LOAD_FAILED",
                "脚本目录加载失败，请查看日志或重试。",
                string.Empty)]);
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
        && SelectedScript is { BuildSucceeded: true, ConfigurationState: ScriptConfigurationState.Ready }
        && SelectedScript.Scope == ScriptScope.Application;

    private bool CanCancel() => IsExecuting;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        var script = SelectedScript;
        if (script is null)
            return;
        await RunScriptAsync(script, useGlobalNotifications: false);
    }

    internal async Task RunFromLauncherAsync(ScriptListItem script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (!script.IsRunnable || script.EntryKind != ScriptEntryKind.Application)
        {
            EventDispatcher.ShowToast("所选脚本不是可运行的 Application 入口。", NotificationType.Warning);
            return;
        }
        if (IsExecuting)
        {
            EventDispatcher.ShowToast("已有脚本正在执行，请稍后再试。", NotificationType.Warning);
            return;
        }

        await RunScriptAsync(script, useGlobalNotifications: true);
    }

    private async Task RunScriptAsync(ScriptListItem script, bool useGlobalNotifications)
    {
        if (script.Descriptor is not { } descriptor)
            return;
        var scope = new ScriptLastArgumentsScope(script.Id, script.EntryKind);
        var lastArguments = await lastArgumentsStore.GetAsync(scope, descriptor);
        var runDialog = services.GetRequiredService<ScriptRunDialogViewModel>();
        runDialog.Initialize(
            descriptor,
            script.Metadata,
            lastArguments?.Arguments,
            lastArguments?.LegacyArgumentsText,
            clearRememberedArguments: () => lastArgumentsStore.ClearAsync(scope));
        var options = await OverlayDialog.ShowCustomModal<ScriptRunOptions>(
            runDialog,
            options: new OverlayDialogOptions
            {
                CanDragMove = false,
                CanResize = false,
                CanLightDismiss = false,
                IsCloseButtonVisible = false,
            });
        if (options is null)
            return;
        IsExecuting = true;
        Status = options.Preview ? $"正在预览运行 {script.Name}" : $"正在运行 {script.Name}";
        using var cancellation = new CancellationTokenSource();
        _executionCancellation = cancellation;
        try
        {
            var outcome = await Task.Run(async () => await scriptManager.ExecuteAsync(
                script.Id,
                CreateExecutionRequest(options),
                options.Timeout,
                cancellation.Token), cancellation.Token);
            if (outcome.Result.Status != ScriptExecutionStatus.Rejected)
            {
                if (options.ApiVersion == ScriptApiVersion.V2)
                    await lastArgumentsStore.SaveV2Async(scope, descriptor, options.Arguments);
                else
                    await lastArgumentsStore.SaveV1Async(scope, options.LegacyArgumentsText ?? string.Empty);
            }
            Status = FormatExecutionStatus(
                script.Name, outcome.Result.Status, outcome.Result.Diagnostics, outcome.Duration, outcome.Result.Effects);
            ShowExecutionNotification(
                Status,
                outcome.Result.Status == ScriptExecutionStatus.Succeeded
                    ? NotificationType.Success
                    : outcome.Result.Status == ScriptExecutionStatus.Cancelled
                        ? NotificationType.Information
                        : NotificationType.Error,
                useGlobalNotifications);
            MarkHistoryDirty();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "运行脚本失败：{ScriptId}", script.Id);
            Status = $"{script.Name} 执行失败，请查看日志";
            ShowExecutionNotification(Status, NotificationType.Error, useGlobalNotifications);
        }
        finally
        {
            _executionCancellation = null;
            ProgressFraction = 0;
            ProgressMessage = string.Empty;
            OnPropertyChanged(nameof(HasProgress));
            IsExecuting = false;
        }
    }

    private void ShowExecutionNotification(
        string message,
        NotificationType type,
        bool useGlobalNotifications)
    {
        if (useGlobalNotifications)
            EventDispatcher.ShowToast(message, type, NotificationRetention.Session, "程序脚本");
        else
            NotificationManager?.Show(message, type);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_executionCancellation is null)
            return;
        Status = "正在取消脚本...";
        _executionCancellation.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanSaveMetadataSettings))]
    private async Task SaveMetadataSettings()
    {
        var script = SelectedScript;
        if (script is null)
            return;
        try
        {
            ImmutableDictionary<string, string> defaultArguments;
            if (DefaultParameterForm is not null)
            {
                if (!DefaultParameterForm.TryBuildMetadataOverrides(
                        requireRequired: script.IsAutomation,
                        out defaultArguments,
                        out var formError))
                {
                    throw new ArgumentException(formError, nameof(DefaultParameterForm));
                }
            }
            else if (!ScriptRunDialogViewModel.TryParseArguments(
                         DefaultArgumentsText,
                         out defaultArguments,
                         out var argumentError))
            {
                throw new ArgumentException(argumentError, nameof(DefaultArgumentsText));
            }
            await ScriptMetadataEditor.WriteAsync(
                script.SourcePath,
                MetadataName,
                MetadataDescription,
                script.IsAutomation ? AutomationScheduleText : null,
                script.IsAutomation && AutomationRunOnStartup,
                GetAutomationTriggers(),
                defaultArguments,
                DefaultTimeoutSeconds,
                updateIdentity: !script.IsCSharp);
            MetadataSettingsError = string.Empty;
            OnPropertyChanged(nameof(HasMetadataSettingsError));
            await ReloadAsync(forceReload: true);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
        {
            MetadataSettingsError = $"保存失败：{exception.Message}";
            OnPropertyChanged(nameof(HasMetadataSettingsError));
        }
    }

    private IReadOnlyList<ScriptAutomationTriggerKind>? GetAutomationTriggers()
    {
        if (SelectedScript?.IsAutomation != true)
            return null;
        var triggers = new List<ScriptAutomationTriggerKind>();
        if (AutomationTriggerOnWorkItemCreated)
            triggers.Add(ScriptAutomationTriggerKind.WorkItemCreated);
        if (AutomationTriggerOnWorkItemSaved)
            triggers.Add(ScriptAutomationTriggerKind.WorkItemSaved);
        if (AutomationTriggerOnTagAdded)
            triggers.Add(ScriptAutomationTriggerKind.TagAdded);
        return triggers;
    }

    private static string FormatStatus(ScriptDirectoryEntry entry)
    {
        if (entry.BuildResult is null)
            return "未加载";
        if (entry.BuildResult.Succeeded)
        {
            if (entry.ConfigurationState == ScriptConfigurationState.NeedsConfiguration)
                return "待配置";
            return entry.BuildResult.Diagnostics.Any(item => item.Code == "SCRIPT_CACHE_HIT")
                ? "已加载（缓存）"
                : "已加载";
        }
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
    private async Task CopyHistoryLog(ScriptHistoryListItem? history)
    {
        if (history is null)
            return;
        if (await CopyStringToClipboardAsync(history.Log))
            NotificationManager?.Show("执行日志已复制", NotificationType.Success);
    }

    [RelayCommand]
    private async Task CopyScriptLogs()
    {
        if (!HasScriptLogs)
            return;
        if (await CopyStringToClipboardAsync(ScriptLogsText))
            NotificationManager?.Show("运行日志已复制", NotificationType.Success);
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
            MarkHistoryDirty();
            Status = "执行历史已清空";
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "清空脚本执行历史失败");
        }
        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanExportScripts))]
    private async Task ExportScripts()
    {
        var dialog = services.GetRequiredService<ScriptShareExportDialogViewModel>();
        dialog.Initialize(Scripts);
        var selection = await OverlayDialog.ShowCustomModal<ScriptShareExportSelection>(
            dialog,
            options: new OverlayDialogOptions
            {
                CanDragMove = false,
                CanResize = false,
                CanLightDismiss = false,
                IsCloseButtonVisible = false,
            });
        if (selection is null || selection.Scripts.Count == 0)
            return;

        var storageProvider = GetStorageProvider();
        if (storageProvider is null)
        {
            NotifyShareResult("导出失败", "当前没有可用的文件选择器。", NotificationType.Error);
            return;
        }
        var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出脚本共享包",
            SuggestedFileName = $"DiaryApp-scripts-{DateTime.Now:yyyyMMdd-HHmmss}{ScriptSharePackageService.FileExtension}",
            DefaultExtension = ScriptSharePackageService.FileExtension.TrimStart('.'),
            FileTypeChoices =
            [
                new FilePickerFileType("DiaryApp 脚本共享包")
                {
                    Patterns = [$"*{ScriptSharePackageService.FileExtension}"],
                },
            ],
        });
        if (file is null)
            return;

        try
        {
            var packagePath = EnsureSharePackageExtension(file.Path.LocalPath);
            await sharePackageService.ExportAsync(
                packagePath,
                _scriptRoot,
                selection.Scripts.Select(script => new ScriptShareExportItem(
                    script.SourcePath,
                    script.Id,
                    script.Name,
                    script.Scope,
                    script.EntryKind,
                    script.Language,
                    script.Metadata,
                    script.BuildSucceeded)).ToArray());
            NotifyShareResult(
                "脚本导出完成",
                $"已导出 {selection.Scripts.Count} 个脚本：{packagePath}",
                NotificationType.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or InvalidOperationException)
        {
            logger.LogError(exception, "导出脚本共享包失败");
            NotifyShareResult("脚本导出失败", exception.Message, NotificationType.Error);
        }
    }

    private static IStorageProvider? GetStorageProvider() =>
        TopLevel.GetTopLevel(App.Instance.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null)?.StorageProvider;

    private static string EnsureSharePackageExtension(string path) =>
        path.EndsWith(ScriptSharePackageService.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? path
            : path + ScriptSharePackageService.FileExtension;

    private void NotifyShareResult(string title, string message, NotificationType type)
    {
        Status = message;
        NotificationManager?.Show($"{title}：{message}", type);
    }

    internal async Task RefreshAfterImportAsync(IReadOnlySet<string> importedIds)
    {
        await ReloadAsync(forceReload: false);
        SelectedScript = VisibleScripts.FirstOrDefault(script => importedIds.Contains(script.Id))
            ?? SelectedScript;
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
            await lastArgumentsStore.ClearScriptAsync(script.Id);
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
        VisibleScripts.ReplaceAll(visible);
        if (SelectedScript is not null && !VisibleScripts.Contains(SelectedScript))
            SelectedScript = VisibleScripts.FirstOrDefault();
        OnPropertyChanged(nameof(HasVisibleScripts));
        OnPropertyChanged(nameof(ShowNoResultsState));
    }

    private void MarkHistoryDirty()
    {
        _historyDirty = true;
        if (SelectedDetailTabIndex == HistoryTabIndex)
            EnsureHistoryLoaded();
    }

    private void EnsureHistoryLoaded()
    {
        if (_historyLoaded && !_historyDirty)
            return;

        RefreshHistory();
        _historyLoaded = true;
        _historyDirty = false;
    }

    private void RefreshHistory()
    {
        History.ReplaceAll(executionHistory.GetRecent().Select(entry =>
        {
            var scriptName = scriptCatalog.TryGet(entry.ScriptId, out var program) && program is not null
                ? program.Descriptor.Name
                : entry.ScriptId;
            return new ScriptHistoryListItem(
                entry.ScriptId,
                scriptName,
                entry.Outcome.Result.Status.ToString(),
                entry.Outcome.Source.ToString(),
                (entry.Outcome.StartedAt ?? entry.RecordedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                $"{entry.Outcome.Duration.TotalMilliseconds:0} ms",
                FormatDiagnostics(entry.Outcome.Result.Diagnostics),
                FormatHistoryLog(entry, scriptName),
                entry.Outcome.Result.Effects);
        }));
        RefreshVisibleHistory();
    }

    private void RefreshVisibleHistory()
    {
        var visible = History.Where(entry =>
                (SelectedHistoryStatusFilter == "全部结果" || entry.StatusLabel == SelectedHistoryStatusFilter)
                && (SelectedHistorySourceFilter == "全部来源" || entry.SourceLabel == SelectedHistorySourceFilter))
            .ToArray();
        VisibleHistory.ReplaceAll(visible);
    }

    private static IReadOnlyList<string> FormatDiagnostics(IEnumerable<ScriptDiagnostic>? diagnostics) =>
        diagnostics?.Select(item => item.SourcePath is null
                ? $"[{item.Code}] {item.Message}"
                : $"[{item.Code}] {item.SourcePath}:{item.Line}:{item.Column} {item.Message}")
            .ToArray()
            ?? Array.Empty<string>();

    private string FormatHistoryLog(ScriptExecutionHistoryEntry entry, string scriptName)
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
        var progress = progressTracker.GetTranscript(outcome.ExecutionId.ToString());
        if (progress.Count > 0)
        {
            lines.Add("进度：");
            lines.AddRange(progress.Select(snapshot => $"  {snapshot.UpdatedAt:HH:mm:ss} {snapshot.DisplayText}"));
        }
        var effectsSummary = ScriptHistoryListItem.FormatEffects(outcome.Result.Effects);
        if (!string.IsNullOrWhiteSpace(effectsSummary))
            lines.Add($"效果：{effectsSummary}");
        lines.Add("诊断：");
        lines.AddRange(FormatDiagnostics(outcome.Result.Diagnostics));
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatExecutionStatus(
        string scriptName,
        ScriptExecutionStatus status,
        IReadOnlyList<ScriptDiagnostic> diagnostics,
        TimeSpan duration,
        ScriptEffectSummary? effects = null) => status switch
        {
            ScriptExecutionStatus.Succeeded => FormatSuccessStatus(scriptName, duration, effects),
            ScriptExecutionStatus.Cancelled => $"{scriptName} 已取消",
            ScriptExecutionStatus.TimedOut => $"{scriptName} 执行超时，请查看诊断详情",
            ScriptExecutionStatus.Rejected => $"{scriptName} 未执行：{diagnostics.FirstOrDefault()?.Message ?? "请求被拒绝"}",
            _ => $"{scriptName} 执行失败：{diagnostics.FirstOrDefault()?.Message ?? "请查看诊断详情"}",
        };

    private static string FormatSuccessStatus(string scriptName, TimeSpan duration, ScriptEffectSummary? effects)
    {
        var summary = ScriptHistoryListItem.FormatEffects(effects);
        return string.IsNullOrWhiteSpace(summary)
            ? $"{scriptName} 执行成功（{duration.TotalSeconds:0.##} 秒）"
            : $"{scriptName} 执行成功（{duration.TotalSeconds:0.##} 秒，{summary}）";
    }

    private static IReadOnlyList<ScriptDiagnosticListItem> FormatDiagnosticDetails(
        IEnumerable<ScriptDiagnostic>? diagnostics) =>
        diagnostics?.Select(FormatDiagnostic).ToArray() ?? Array.Empty<ScriptDiagnosticListItem>();

    internal static IReadOnlyList<ScriptListItem> CreateScriptItems(ScriptDirectoryLoadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Entries.Select(entry =>
        {
            var descriptor = entry.DiscoveredDescriptor ?? entry.BuildResult?.Program?.Descriptor;
            var entryKind = descriptor is null
                ? ScriptEntryKind.Application
                : ScriptEntryKindResolver.Resolve(descriptor);
            var entryDiagnostics = (entry.BuildResult?.Diagnostics ?? []).AddRange(entry.ConfigurationDiagnostics);
            return new ScriptListItem(
                entry.SourcePath,
                descriptor?.Id ?? Path.GetFileNameWithoutExtension(entry.SourcePath),
                descriptor?.Name ?? Path.GetFileName(entry.SourcePath),
                entry.Scope,
                entry.BuildResult?.Succeeded == true,
                FormatStatus(entry),
                FormatDiagnostics(entryDiagnostics),
                FormatDiagnosticDetails(entryDiagnostics),
                descriptor?.Description ?? string.Empty,
                entryKind,
                entry.Metadata,
                descriptor,
                entry.ConfigurationState);
        }).ToArray();
    }

    private static PreparedScriptDirectoryData PrepareDirectoryResult(ScriptDirectoryLoadResult result)
    {
        var diagnostics = result.Diagnostics.Select(FormatDiagnostic).ToArray();
        var scripts = CreateScriptItems(result);
        return new PreparedScriptDirectoryData(
            result,
            diagnostics,
            scripts,
            result.Diagnostics.Count(item => item.Severity == ScriptDiagnosticSeverity.Error));
    }

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

    private sealed record PreparedScriptDirectoryData(
        ScriptDirectoryLoadResult Source,
        IReadOnlyList<ScriptDiagnosticListItem> Diagnostics,
        IReadOnlyList<ScriptListItem> Scripts,
        int ErrorCount)
    {
        public TimeSpan DirectoryElapsed { get; init; }
        public TimeSpan PrepareElapsed { get; init; }
    }

    private static ScriptExecutionRequest CreateExecutionRequest(ScriptRunOptions options) =>
        new(
            Arguments: options.Arguments,
            Source: ScriptExecutionSource.Manual,
            IdempotencyKey: options.IdempotencyKey,
            Preview: options.Preview);
}
