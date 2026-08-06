using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.GUIBase.Utils;
using Diary.GUIBase;
using Diary.PluginUI;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.RedMine.UI.ViewModels;

[DiAutoRegister]
public partial class RedMineConfigurationViewModel : ViewModelBase, ITrackerSettingsPage
{
    private RedMinePluginConfigurationEditSession? _session;
    private RedMinePluginConfig? Configuration => _session?.WorkingCopy;

    public ObservableCollection<RedMineInstanceSettings> Instances { get; } = new();
    [ObservableProperty] private RedMineTagRuleEditorViewModel? _tagRuleEditor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveInstance))]
    [NotifyPropertyChangedFor(nameof(HasSelectedInstance))]
    private RedMineInstanceSettings? _selectedInstance;

    [ObservableProperty] private SettingGroup _instanceSettings = new("实例配置");

    public bool CanRemoveInstance => SelectedInstance is not null && Instances.Count > 1;
    public bool HasSelectedInstance => SelectedInstance is not null;

    public void InitSettings(RedMinePluginConfigurationEditSession session)
    {
        _session = session;
        Logging.Logger.LogDebug("初始化 RedMine 多实例设置：配置实例数 {Count}", session.WorkingCopy.Instances.Count);
        Reload();
    }

    public void Reload()
    {
        if (_session is null)
            return;

        _session.Reload();
        var configuration = _session.WorkingCopy;
        Logging.Logger.LogDebug("重载 RedMine 多实例设置：重载前配置实例数 {Count}", configuration.Instances.Count);
        Instances.Clear();
        foreach (var instance in configuration.Instances)
            Instances.Add(instance);
        SelectedInstance = Instances.FirstOrDefault();
        RebuildInstanceSettings();
        Logging.Logger.LogDebug("重载 RedMine 多实例设置完成：显示实例数 {Count}，当前实例 {InstanceId}",
            Instances.Count, SelectedInstance?.InstanceId ?? "<none>");
    }

    partial void OnSelectedInstanceChanged(RedMineInstanceSettings? value)
    {
        RebuildInstanceSettings();
        TagRuleEditor = value is null ? null : new RedMineTagRuleEditorViewModel(value);
    }

    public void Save()
    {
        InstanceSettings.Save();
        _session?.Commit();
        Reload();
    }

    private void RebuildInstanceSettings()
    {
        var settings = new SettingGroup("实例配置");
        if (SelectedInstance is not null)
        {
            SettingTreeBuilder.BuildTree(settings, SelectedInstance, BaseApp.Instance);
            settings.Load();
        }
        InstanceSettings = settings;
    }

    [RelayCommand]
    private void AddInstance()
    {
        if (Configuration is null)
            return;

        var instance = new RedMineInstanceSettings
        {
            InstanceId = CreateInstanceId(),
            DisplayName = $"RedMine实例 {Instances.Count + 1}",
            Enabled = false,
        };
        Configuration.Instances.Add(instance);
        Instances.Add(instance);
        SelectedInstance = instance;
    }

    [RelayCommand]
    private void RemoveInstance()
    {
        if (Configuration is null || SelectedInstance is null || !CanRemoveInstance)
            return;

        var index = Instances.IndexOf(SelectedInstance);
        Configuration.Instances.Remove(SelectedInstance);
        Instances.Remove(SelectedInstance);
        SelectedInstance = Instances[Math.Clamp(index, 0, Instances.Count - 1)];
    }

    private string CreateInstanceId()
    {
        var index = 1;
        while (Instances.Any(x => x.InstanceId == $"redmine.instance{index}"))
            index++;
        return $"redmine.instance{index}";
    }
}
