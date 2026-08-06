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
    private RedMinePluginConfig? _configuration;

    public ObservableCollection<RedMineInstanceSettings> Instances { get; } = new();
    [ObservableProperty] private RedMineTagRuleEditorViewModel? _tagRuleEditor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveInstance))]
    [NotifyPropertyChangedFor(nameof(HasSelectedInstance))]
    private RedMineInstanceSettings? _selectedInstance;

    [ObservableProperty] private SettingGroup _instanceSettings = new("实例配置");

    public bool CanRemoveInstance => SelectedInstance is not null && Instances.Count > 1;
    public bool HasSelectedInstance => SelectedInstance is not null;

    public void InitSettings(RedMinePluginConfig configuration)
    {
        _configuration = configuration;
        Logging.Logger.LogDebug("初始化 RedMine 多实例设置：配置实例数 {Count}", configuration.Instances.Count);
        Reload();
    }

    public void Reload()
    {
        if (_configuration is null)
            return;

        Logging.Logger.LogDebug("重载 RedMine 多实例设置：重载前配置实例数 {Count}", _configuration.Instances.Count);
        Instances.Clear();
        foreach (var instance in _configuration.Instances)
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
        => InstanceSettings.Save();

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
        if (_configuration is null)
            return;

        var instance = new RedMineInstanceSettings
        {
            InstanceId = CreateInstanceId(),
            DisplayName = $"RedMine实例 {Instances.Count + 1}",
            Enabled = false,
        };
        _configuration.Instances.Add(instance);
        Instances.Add(instance);
        SelectedInstance = instance;
    }

    [RelayCommand]
    private void RemoveInstance()
    {
        if (_configuration is null || SelectedInstance is null || !CanRemoveInstance)
            return;

        var index = Instances.IndexOf(SelectedInstance);
        _configuration.Instances.Remove(SelectedInstance);
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
