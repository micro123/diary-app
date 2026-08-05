using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.GUIBase.Utils;
using Diary.GUIBase;
using Diary.Core.Utils;
using Diary.PluginUI;
using Diary.RedMine;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using Diary.RedMine.Models;

namespace Diary.RedMine.UI.ViewModels;

public sealed record RedMineTagRuleOption(int Id, string Name);

public sealed partial class RedMineTagRuleViewModel : ObservableObject
{
    public RedMineTagRule Rule { get; }
    public IReadOnlyList<RedMineTagRuleOption> Tags { get; }
    public IReadOnlyList<RedMineTagRuleOption> Activities { get; }
    public IReadOnlyList<RedMineTagRuleOption> Issues { get; }

    [ObservableProperty] private RedMineTagRuleOption? _selectedTag;
    [ObservableProperty] private RedMineTagRuleOption? _selectedActivity;
    [ObservableProperty] private RedMineTagRuleOption? _selectedIssue;

    public bool Enabled
    {
        get => Rule.Enabled;
        set => SetProperty(Rule.Enabled, value, Rule, static (rule, enabled) => rule.Enabled = enabled);
    }

    public int Priority
    {
        get => Rule.Priority;
        set => SetProperty(Rule.Priority, value, Rule, static (rule, priority) => rule.Priority = priority);
    }

    public RedMineTagRuleViewModel(
        RedMineTagRule rule,
        IReadOnlyList<RedMineTagRuleOption> tags,
        IReadOnlyList<RedMineTagRuleOption> activities,
        IReadOnlyList<RedMineTagRuleOption> issues)
    {
        Rule = rule;
        Tags = tags;
        Activities = activities;
        Issues = issues;
        SelectedTag = ResolveOption(tags, rule.TagId, "已删除标签");
        SelectedActivity = ResolveOption(activities, rule.ActivityId ?? 0, "无效活动");
        SelectedIssue = ResolveOption(issues, rule.IssueId ?? 0, "无效问题");
    }

    partial void OnSelectedTagChanged(RedMineTagRuleOption? value)
        => Rule.TagId = value?.Id ?? 0;

    partial void OnSelectedActivityChanged(RedMineTagRuleOption? value)
        => Rule.ActivityId = value is { Id: > 0 } ? value.Id : null;

    partial void OnSelectedIssueChanged(RedMineTagRuleOption? value)
        => Rule.IssueId = value is { Id: > 0 } ? value.Id : null;

    private static RedMineTagRuleOption? ResolveOption(
        IReadOnlyList<RedMineTagRuleOption> options,
        int id,
        string missingName)
        => options.FirstOrDefault(option => option.Id == id)
           ?? (id > 0 ? new RedMineTagRuleOption(id, $"{missingName} #{id}") : options.FirstOrDefault());
}

[DiAutoRegister]
public partial class RedMineConfigurationViewModel : ViewModelBase, ITrackerSettingsPage
{
    private RedMinePluginConfig? _configuration;

    public ObservableCollection<RedMineInstanceSettings> Instances { get; } = new();
    public ObservableCollection<RedMineTagRuleViewModel> TagRules { get; } = new();

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
        ReloadTagRules();
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

    private void ReloadTagRules()
    {
        TagRules.Clear();
        if (SelectedInstance is null)
            return;

        var tags = BaseApp.Instance.UseDb?.AllWorkTags()
            .Where(tag => !tag.Disabled)
            .Select(tag => new RedMineTagRuleOption(tag.Id, tag.Name))
            .ToArray() ?? Array.Empty<RedMineTagRuleOption>();
        var activities = new List<RedMineTagRuleOption> { new(0, "不设置活动") };
        var issues = new List<RedMineTagRuleOption> { new(0, "不设置问题") };
        try
        {
            var database = BaseApp.Instance.UseDb?.GetExtension<IRedMineDb>(
                SelectedInstance.InstanceId,
                new RedMinePlugin().GetMigrations());
            if (database is not null)
            {
                activities.AddRange(database.GetRedMineActivities()
                    .Select(activity => new RedMineTagRuleOption(activity.Id, activity.Title)));
                issues.AddRange(database.GetRedMineIssues(null)
                    .Select(issue => new RedMineTagRuleOption(issue.Id, $"#{issue.Id} {issue.Title}")));
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.LogWarning(ex, "加载 RedMine 标签规则选项失败：实例 {InstanceId}", SelectedInstance.InstanceId);
        }

        foreach (var rule in SelectedInstance.TagRules)
            TagRules.Add(new RedMineTagRuleViewModel(rule, tags, activities, issues));
    }

    [RelayCommand]
    private void AddTagRule()
    {
        if (SelectedInstance is null)
            return;
        var tags = BaseApp.Instance.UseDb?.AllWorkTags().Where(tag => !tag.Disabled).ToArray();
        if (tags is not { Length: > 0 })
            return;
        var rule = new RedMineTagRule { TagId = tags[0].Id };
        SelectedInstance.TagRules.Add(rule);
        ReloadTagRules();
    }

    [RelayCommand]
    private void RemoveTagRule(RedMineTagRuleViewModel rule)
    {
        if (SelectedInstance is null)
            return;
        SelectedInstance.TagRules.Remove(rule.Rule);
        TagRules.Remove(rule);
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
