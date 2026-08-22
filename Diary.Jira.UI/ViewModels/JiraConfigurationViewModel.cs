using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;

namespace Diary.Jira.UI.ViewModels;

[DiAutoRegister]
public partial class JiraConfigurationViewModel : ViewModelBase, ITrackerSettingsPage
{
    private JiraPluginConfigurationEditSession? _session;
    private JiraPluginConfig? Configuration => _session?.WorkingCopy;
    public ObservableCollection<JiraInstanceSettings> Instances { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveInstance))]
    [NotifyPropertyChangedFor(nameof(HasSelectedInstance))]
    private JiraInstanceSettings? _selectedInstance;

    [ObservableProperty] private SettingGroup _instanceSettings = new("实例配置");
    [ObservableProperty] private bool _testingConnection;
    [ObservableProperty] private string _connectionMessage = string.Empty;
    public bool CanRemoveInstance => SelectedInstance is not null && Instances.Count > 1;
    public bool HasSelectedInstance => SelectedInstance is not null;

    public void InitSettings(JiraPluginConfigurationEditSession session)
    {
        _session = session;
        Reload();
    }

    public void Reload()
    {
        if (_session is null) return;
        _session.Reload();
        Instances.Clear();
        foreach (var instance in _session.WorkingCopy.Instances) Instances.Add(instance);
        SelectedInstance = Instances.FirstOrDefault();
        RebuildSettings();
    }

    partial void OnSelectedInstanceChanged(JiraInstanceSettings? value) => RebuildSettings();

    public void Save()
    {
        InstanceSettings.Save();
        _session?.Commit();
        Reload();
    }

    private void RebuildSettings()
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
    private async Task TestConnectionAsync()
    {
        if (SelectedInstance is null) return;
        try
        {
            TestingConnection = true;
            using var api = new JiraApi(SelectedInstance);
            var result = await api.SearchProjectsAsync(maxResults: 1);
            ConnectionMessage = result.Success ? "Jira 连接成功。" : result.Error ?? "Jira 连接失败。";
        }
        catch (Exception exception)
        {
            ConnectionMessage = $"Jira 连接失败：{exception.Message}";
        }
        finally
        {
            TestingConnection = false;
        }
    }

    [RelayCommand]
    private void AddInstance()
    {
        if (Configuration is null) return;
        var instance = new JiraInstanceSettings
        {
            InstanceId = CreateInstanceId(),
            DisplayName = $"Jira 实例 {Instances.Count + 1}",
        };
        Configuration.Instances.Add(instance);
        Instances.Add(instance);
        SelectedInstance = instance;
    }

    [RelayCommand]
    private void RemoveInstance()
    {
        if (Configuration is null || SelectedInstance is null || !CanRemoveInstance) return;
        var index = Instances.IndexOf(SelectedInstance);
        Configuration.Instances.Remove(SelectedInstance);
        Instances.Remove(SelectedInstance);
        SelectedInstance = Instances[Math.Clamp(index, 0, Instances.Count - 1)];
    }

    private string CreateInstanceId()
    {
        var index = 1;
        while (Instances.Any(item => item.InstanceId == $"jira.instance{index}")) index++;
        return $"jira.instance{index}";
    }
}
