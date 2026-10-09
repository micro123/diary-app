using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.ModuleBase;
using Diary.ModuleUI;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;

namespace Diary.App.ViewModels.Dialogs;

public sealed partial class ModuleStateItemViewModel : ObservableObject
{
    private readonly bool _initialEnabled;

    public ModuleStateItemViewModel(
        AppModuleManifest manifest,
        bool enabled,
        bool loaded,
        AppModuleDiagnostic? diagnostic)
    {
        Id = manifest.Id;
        DisplayName = manifest.DisplayName;
        Version = manifest.Version;
        EnabledByDefault = manifest.EnabledByDefault;
        IsLoaded = loaded;
        DiagnosticCode = diagnostic?.Code ?? string.Empty;
        DiagnosticMessage = diagnostic?.Message ?? "尚无启动诊断。";
        _initialEnabled = enabled;
        _isEnabled = enabled;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Version { get; }

    public bool EnabledByDefault { get; }

    public bool IsLoaded { get; }

    public string DiagnosticCode { get; }

    public string DiagnosticMessage { get; }

    public string RuntimeStatus => IsLoaded ? "当前已加载" : "当前未加载";

    public bool RestartRequired => IsEnabled != _initialEnabled;

    public string ChangeStatus => RestartRequired ? "启用状态已修改，重启后生效" : "未修改";

    [ObservableProperty] private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(RestartRequired));
        OnPropertyChanged(nameof(ChangeStatus));
    }
}

public sealed record ModuleDiagnosticItem(
    string ModuleId,
    string State,
    string Code,
    string Message);

[DiAutoRegister]
public partial class ModuleSettingsDialogViewModel : ViewModelBase, IDialogContext
{
    private readonly ModuleStateStore _stateStore;
    private readonly Dictionary<string, bool> _savedStates;

    public ModuleSettingsDialogViewModel(
        AppModuleCatalog catalog,
        SettingsContributionRegistry settingsRegistry)
    {
        _stateStore = new ModuleStateStore(catalog.StateFilePath);
        var state = _stateStore.Load();
        _savedStates = new Dictionary<string, bool>(state.ModuleStates, StringComparer.Ordinal);
        CanSave = !state.IsCorrupt;
        Status = state.IsCorrupt
            ? $"模块状态文件不可读取，已阻止覆盖：{state.Error}"
            : "模块启用状态修改后需要重启应用；模块自身设置由对应页面保存。";

        var loadedIds = catalog.LoadedManifests
            .Select(manifest => manifest.Id)
            .ToHashSet(StringComparer.Ordinal);
        var latestDiagnostics = catalog.Diagnostics
            .GroupBy(item => item.ModuleId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        foreach (var manifest in catalog.DiscoveredManifests
                     .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            latestDiagnostics.TryGetValue(manifest.Id, out var diagnostic);
            Modules.Add(new ModuleStateItemViewModel(
                manifest,
                state.IsEnabled(manifest),
                loadedIds.Contains(manifest.Id),
                diagnostic));
        }

        foreach (var diagnostic in catalog.Diagnostics)
        {
            Diagnostics.Add(new ModuleDiagnosticItem(
                diagnostic.ModuleId,
                diagnostic.State.ToString(),
                diagnostic.Code,
                diagnostic.Message));
        }

        foreach (var page in settingsRegistry.GetPages())
            SettingsPages.Add(page);
    }

    public ObservableCollection<ModuleStateItemViewModel> Modules { get; } = [];

    public ObservableCollection<ModuleDiagnosticItem> Diagnostics { get; } = [];

    public ObservableCollection<SettingsContributionPage> SettingsPages { get; } = [];

    public bool HasNoModules => Modules.Count == 0;

    public bool HasNoSettingsPages => SettingsPages.Count == 0;

    public bool CanSave { get; }

    [ObservableProperty] private string _status = string.Empty;

    public event EventHandler<object?>? RequestClose;

    public void Close() => RequestClose?.Invoke(this, null);

    [RelayCommand]
    private void Save()
    {
        if (!CanSave)
            return;
        try
        {
            var states = new Dictionary<string, bool>(_savedStates, StringComparer.Ordinal);
            foreach (var module in Modules)
                states[module.Id] = module.IsEnabled;
            _stateStore.Save(states);
            NotificationManager?.Show("模块启用状态已保存，重启应用后生效");
            RequestClose?.Invoke(this, true);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            Status = $"保存失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);
}
