using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.PluginBase;

namespace Diary.App.ViewModels;

/// <summary>设置页展示的插件诊断条目，不向 UI 暴露具体 tracker 类型。</summary>
public sealed class TrackerPluginDiagnosticViewModel : ObservableObject
{
    private readonly Action _retry;
    private readonly Action _toggle;

    public TrackerPluginDiagnosticViewModel(
        TrackerPluginDiagnosticEntry entry,
        Action retry,
        Action toggle)
    {
        PluginId = entry.PluginId;
        PluginVersion = entry.PluginVersion;
        PluginState = entry.PluginState.ToString();
        InstanceId = entry.InstanceId ?? "-";
        DisplayName = entry.DisplayName ?? entry.InstanceId ?? "插件整体状态";
        InstanceState = entry.InstanceState?.ToString() ?? "-";
        Error = entry.Error ?? entry.PluginError ?? string.Empty;
        CanRetry = entry.CanRetry && entry.InstanceId is not null;
        CanToggle = entry.CanToggle && entry.InstanceId is not null;
        IsEnabled = entry.InstanceState == TrackerInstanceState.Enabled;
        _retry = retry;
        _toggle = toggle;
        RetryCommand = new RelayCommand(() => _retry(), () => CanRetry);
        ToggleCommand = new RelayCommand(() => _toggle(), () => CanToggle);
    }

    public string PluginId { get; }
    public string PluginVersion { get; }
    public string PluginState { get; }
    public string InstanceId { get; }
    public string DisplayName { get; }
    public string InstanceState { get; }
    public string Error { get; }
    public bool CanRetry { get; }
    public bool CanToggle { get; }
    public bool IsEnabled { get; }
    public string ToggleText => IsEnabled ? "禁用" : "启用";
    public ICommand RetryCommand { get; }
    public ICommand ToggleCommand { get; }
}
