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
    public bool ShowTagSelector { get; }

    [ObservableProperty] private RedMineTagRuleOption? _selectedTag;
    [ObservableProperty] private RedMineTagRuleOption? _selectedActivity;
    [ObservableProperty] private RedMineTagRuleOption? _selectedIssue;

    public bool Enabled
    {
        get => Rule.Enabled;
        set => SetProperty(Rule.Enabled, value, Rule, static (rule, enabled) => rule.Enabled = enabled);
    }

    public bool ForceOverwrite
    {
        get => Rule.ForceOverwrite;
        set => SetProperty(
            Rule.ForceOverwrite,
            value,
            Rule,
            static (rule, forceOverwrite) => rule.ForceOverwrite = forceOverwrite);
    }

    public RedMineTagRuleViewModel(
        RedMineTagRule rule,
        IReadOnlyList<RedMineTagRuleOption> tags,
        IReadOnlyList<RedMineTagRuleOption> activities,
        IReadOnlyList<RedMineTagRuleOption> issues,
        bool showTagSelector = true)
    {
        Rule = rule;
        ShowTagSelector = showTagSelector;
        Tags = AddMissingOption(tags, rule.TagId, "已删除标签");
        Activities = AddMissingOption(activities, rule.ActivityId ?? 0, "无效活动");
        Issues = AddMissingOption(issues, rule.IssueId ?? 0, "无效问题");
        SelectedTag = ResolveOption(Tags, rule.TagId);
        SelectedActivity = ResolveOption(Activities, rule.ActivityId ?? 0);
        SelectedIssue = ResolveOption(Issues, rule.IssueId ?? 0);
    }

    partial void OnSelectedTagChanged(RedMineTagRuleOption? value)
        => Rule.TagId = value?.Id ?? 0;

    partial void OnSelectedActivityChanged(RedMineTagRuleOption? value)
        => Rule.ActivityId = value is { Id: > 0 } ? value.Id : null;

    partial void OnSelectedIssueChanged(RedMineTagRuleOption? value)
        => Rule.IssueId = value is { Id: > 0 } ? value.Id : null;

    private static IReadOnlyList<RedMineTagRuleOption> AddMissingOption(
        IReadOnlyList<RedMineTagRuleOption> options,
        int id,
        string missingName)
        => id <= 0 || options.Any(option => option.Id == id)
            ? options
            : options.Append(new RedMineTagRuleOption(id, $"{missingName} #{id}")).ToArray();

    private static RedMineTagRuleOption? ResolveOption(
        IReadOnlyList<RedMineTagRuleOption> options,
        int id)
        => options.FirstOrDefault(option => option.Id == id) ?? options.FirstOrDefault();
}

public sealed partial class RedMineTagRuleEditorViewModel : ViewModelBase
{
    private RedMineInstanceSettings _settings;
    private int? _tagId;
    private IReadOnlyList<RedMineTagRuleOption> _availableTags = Array.Empty<RedMineTagRuleOption>();

    public ObservableCollection<RedMineTagRuleViewModel> TagRules { get; } = new();
    public string Title => _tagId is null ? "Redmine 标签自动规则" : _settings.DisplayName;
    public bool ShowNoTagsHint => !CanAddTagRule();

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

    public void Reset(RedMineInstanceSettings settings)
    {
        _settings = settings;
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
        _availableTags = tags;
        OnPropertyChanged(nameof(ShowNoTagsHint));
        AddTagRuleCommand.NotifyCanExecuteChanged();
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
                    .Select(issue => new RedMineTagRuleOption(
                        issue.Id,
                        $"#{issue.Id} {issue.Title}{(issue.Disabled ? " [无效]" : string.Empty)}")));
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.LogWarning(ex, "加载 RedMine 标签规则选项失败：实例 {InstanceId}", _settings.InstanceId);
        }

        foreach (var rule in _settings.TagRules.Where(rule => _tagId is null || rule.TagId == _tagId))
            TagRules.Add(new RedMineTagRuleViewModel(
                rule,
                tags,
                activities,
                issues,
                showTagSelector: _tagId is null));
    }

    [RelayCommand(CanExecute = nameof(CanAddTagRule))]
    private void AddTagRule()
    {
        var tagId = _tagId ?? _availableTags.FirstOrDefault()?.Id;
        if (tagId is null)
            return;
        _settings.TagRules.Add(new RedMineTagRule
        {
            TagId = tagId.Value,
            ForceOverwrite = true,
        });
        Reload();
    }

    private bool CanAddTagRule() => _tagId is not null || _availableTags.Count > 0;

    [RelayCommand]
    private void RemoveTagRule(RedMineTagRuleViewModel rule)
    {
        _settings.TagRules.Remove(rule.Rule);
        TagRules.Remove(rule);
    }
}

public sealed class RedMineTagRuleEditorContribution : ITagRuleEditorContribution
{
    private static readonly HashSet<string> SupportedValueKeys =
        ["activityId", "issueId", "enabled", "forceOverwrite"];
    private readonly RedMineInstanceConfigurationEditSession _session;
    private readonly RedMineTagRuleEditorViewModel _view;

    public RedMineTagRuleEditorContribution(RedMineInstanceConfigurationEditSession session)
    {
        _session = session;
        _view = new RedMineTagRuleEditorViewModel(session.WorkingCopy);
    }

    public string PluginId => RedMinePluginConstants.PluginId;
    public string InstanceId => _session.WorkingCopy.InstanceId;
    public string InstanceName => _session.WorkingCopy.DisplayName;
    public ViewModelBase View => _view;
    public void SelectTag(WorkTag tag) => _view.SelectTag(tag);

    public IReadOnlyCollection<TrackerTagRulePackageItem> ExportRules(
        IReadOnlyDictionary<int, string> tagKeys)
        => _session.WorkingCopy.TagRules
            .Where(rule => tagKeys.ContainsKey(rule.TagId))
            .Select(rule => new TrackerTagRulePackageItem(
                tagKeys[rule.TagId],
                ExportRuleValues(rule)))
            .ToArray();

    internal static IReadOnlyDictionary<string, string?> ExportRuleValues(RedMineTagRule rule)
        => new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["activityId"] = rule.ActivityId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["issueId"] = rule.IssueId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["enabled"] = rule.Enabled.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["forceOverwrite"] = rule.ForceOverwrite.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    internal static bool ReadForceOverwrite(IReadOnlyDictionary<string, string?> values)
        => values.TryGetValue("forceOverwrite", out var text)
            && bool.TryParse(text, out var parsed)
            && parsed;

    public IReadOnlyCollection<TrackerTagRuleValidation> ValidateImportRules(
        IReadOnlyCollection<TrackerTagRulePackageItem> rules,
        IReadOnlyDictionary<string, int> tagIds)
    {
        HashSet<int> activityIds;
        HashSet<int> issueIds;
        try
        {
            var database = BaseApp.Instance.UseDb?.GetExtension<IRedMineDb>(
                InstanceId,
                new RedMinePlugin().GetMigrations());
            if (database is null)
                return rules.Select(rule => Unavailable(rule, "本地 RedMine 数据不可用，无法验证规则目标。"))
                    .ToArray();
            activityIds = database.GetRedMineActivities()
                .Where(activity => !activity.Invalid)
                .Select(activity => activity.Id)
                .ToHashSet();
            issueIds = database.GetRedMineIssues(null)
                .Where(issue => !issue.Disabled && !issue.Invalid)
                .Select(issue => issue.Id)
                .ToHashSet();
        }
        catch (Exception exception)
        {
            return rules.Select(rule => Unavailable(rule, $"读取本地 RedMine 数据失败：{exception.Message}"))
                .ToArray();
        }

        return rules.Select(rule => ValidateRule(rule, tagIds, activityIds, issueIds)).ToArray();
    }

    public int ImportRules(
        IReadOnlyCollection<TrackerTagRulePackageItem> rules,
        IReadOnlyDictionary<string, int> tagIds)
    {
        var imported = 0;
        foreach (var item in rules)
        {
            if (!tagIds.TryGetValue(item.TagKey, out var tagId)
                || !TryReadOptionalPositiveInt(item.Values, "activityId", out var activityId)
                || !TryReadOptionalPositiveInt(item.Values, "issueId", out var issueId)
                || activityId is null && issueId is null)
                continue;
            var enabled = !item.Values.TryGetValue("enabled", out var enabledText)
                || !bool.TryParse(enabledText, out var parsedEnabled)
                || parsedEnabled;
            var forceOverwrite = ReadForceOverwrite(item.Values);
            if (_session.WorkingCopy.TagRules.Any(rule => rule.TagId == tagId
                && rule.ActivityId == activityId
                && rule.IssueId == issueId))
                continue;
            _session.WorkingCopy.TagRules.Add(new RedMineTagRule
            {
                RuleId = Guid.NewGuid().ToString("N"),
                TagId = tagId,
                ActivityId = activityId,
                IssueId = issueId,
                Enabled = enabled,
                ForceOverwrite = forceOverwrite,
            });
            imported++;
        }
        _view.Reset(_session.WorkingCopy);
        return imported;
    }

    private static TrackerTagRuleValidation ValidateRule(
        TrackerTagRulePackageItem rule,
        IReadOnlyDictionary<string, int> tagIds,
        IReadOnlySet<int> activityIds,
        IReadOnlySet<int> issueIds)
    {
        if (!tagIds.ContainsKey(rule.TagKey))
            return Invalid(rule, "规则引用的标签不存在或未选择导入。");
        var unsupported = rule.Values.Keys.Where(key => !SupportedValueKeys.Contains(key)).ToArray();
        if (unsupported.Length > 0)
            return Invalid(rule, $"包含不支持的字段：{string.Join("、", unsupported)}。");
        if (!TryReadOptionalPositiveInt(rule.Values, "activityId", out var activityId)
            || !TryReadOptionalPositiveInt(rule.Values, "issueId", out var issueId))
            return Invalid(rule, "Activity 或 Issue ID 必须是正整数。");
        if (activityId is null && issueId is null)
            return Invalid(rule, "规则没有配置 Activity 或 Issue 目标。");
        if (activityId is not null && !activityIds.Contains(activityId.Value))
            return Invalid(rule, $"Activity #{activityId} 不存在或已失效。");
        if (issueId is not null && !issueIds.Contains(issueId.Value))
            return Invalid(rule, $"Issue #{issueId} 不存在或已失效。");
        if (rule.Values.TryGetValue("enabled", out var enabled)
            && enabled is not null
            && !bool.TryParse(enabled, out _))
            return Invalid(rule, "enabled 必须是布尔值。");
        if (rule.Values.TryGetValue("forceOverwrite", out var forceOverwrite)
            && forceOverwrite is not null
            && !bool.TryParse(forceOverwrite, out _))
            return Invalid(rule, "forceOverwrite 必须是布尔值。");
        return new TrackerTagRuleValidation(rule, TrackerTagRuleValidationState.Valid, "规则有效");
    }

    private static bool TryReadOptionalPositiveInt(
        IReadOnlyDictionary<string, string?> values,
        string key,
        out int? value)
    {
        value = null;
        if (!values.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
            return true;
        if (!int.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
            return false;
        value = parsed;
        return true;
    }

    private static TrackerTagRuleValidation Invalid(TrackerTagRulePackageItem rule, string message)
        => new(rule, TrackerTagRuleValidationState.Invalid, message);

    private static TrackerTagRuleValidation Unavailable(TrackerTagRulePackageItem rule, string message)
        => new(rule, TrackerTagRuleValidationState.Unavailable, message);

    public void Commit()
    {
        _session.Commit();
        _view.Reset(_session.WorkingCopy);
    }
    public void Reload()
    {
        _session.Reload();
        _view.Reset(_session.WorkingCopy);
    }
}
