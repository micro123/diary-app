using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Core.Data.Base;
using Diary.GUIBase;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;
using Microsoft.Extensions.Logging;

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

public sealed partial class RedMineTagRuleEditorViewModel : ViewModelBase
{
    private readonly RedMineInstanceSettings _settings;
    private int? _tagId;

    public ObservableCollection<RedMineTagRuleViewModel> TagRules { get; } = new();
    public string Title => _tagId is null ? "RedMine 标签自动规则" : _settings.DisplayName;

    public RedMineTagRuleEditorViewModel(RedMineInstanceSettings settings, int? tagId = null)
    {
        _settings = settings;
        _tagId = tagId;
        Reload();
    }

    public void SelectTag(WorkTag tag)
    {
        _tagId = tag.Id;
        OnPropertyChanged(nameof(Title));
        Reload();
    }

    private void Reload()
    {
        TagRules.Clear();
        var tags = BaseApp.Instance.UseDb?.AllWorkTags()
            .Where(tag => !tag.Disabled)
            .Select(tag => new RedMineTagRuleOption(tag.Id, tag.Name))
            .ToArray() ?? Array.Empty<RedMineTagRuleOption>();
        var activities = new List<RedMineTagRuleOption> { new(0, "不设置活动") };
        var issues = new List<RedMineTagRuleOption> { new(0, "不设置问题") };
        try
        {
            var database = BaseApp.Instance.UseDb?.GetExtension<IRedMineDb>(
                _settings.InstanceId,
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
            Logging.Logger.LogWarning(ex, "加载 RedMine 标签规则选项失败：实例 {InstanceId}", _settings.InstanceId);
        }

        foreach (var rule in _settings.TagRules.Where(rule => _tagId is null || rule.TagId == _tagId))
            TagRules.Add(new RedMineTagRuleViewModel(rule, tags, activities, issues));
    }

    [RelayCommand]
    private void AddTagRule()
    {
        var tagId = _tagId ?? BaseApp.Instance.UseDb?.AllWorkTags()
            .FirstOrDefault(tag => !tag.Disabled)?.Id;
        if (tagId is null)
            return;
        _settings.TagRules.Add(new RedMineTagRule { TagId = tagId.Value });
        Reload();
    }

    [RelayCommand]
    private void RemoveTagRule(RedMineTagRuleViewModel rule)
    {
        _settings.TagRules.Remove(rule.Rule);
        TagRules.Remove(rule);
    }
}

public sealed class RedMineTagRuleEditorContribution(
    RedMineInstanceSettings settings) : ITagRuleEditorContribution
{
    private readonly RedMineTagRuleEditorViewModel _view = new(settings);

    public string PluginId => RedMinePluginConstants.PluginId;
    public string InstanceId => settings.InstanceId;
    public ViewModelBase View => _view;
    public void SelectTag(WorkTag tag) => _view.SelectTag(tag);
}
