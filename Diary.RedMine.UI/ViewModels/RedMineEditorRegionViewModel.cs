using CommunityToolkit.Mvvm.ComponentModel;
using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.RedMine.Models;

namespace Diary.RedMine.UI.ViewModels;

public partial class RedMineEditorRegionViewModel : ViewModelBase, ITrackerEditorExtension, ITrackerTagDefaults
{
    private readonly IRedMineUiData _data;
    private readonly IRedMineApi _api;
    private WorkTimeEntry? TimeEntry { get; set; }

    [ObservableProperty]
    private IReadOnlyList<RedMineIssueDisplay> _redMineIssues = Array.Empty<RedMineIssueDisplay>();
    [ObservableProperty]
    private IReadOnlyList<RedMineActivity> _redMineActivities = Array.Empty<RedMineActivity>();

    [ObservableProperty] private int _issueIndex = -1;
    [ObservableProperty] private int _activityIndex = -1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _uploaded;
    [ObservableProperty] private string _issueText = string.Empty;

    public bool IsLocked => Uploaded;
    public TrackerUploadState UploadState => TimeEntry?.UploadState ?? TrackerUploadState.NotAttempted;
    public string? UploadError => TimeEntry?.UploadError;
    public DateTimeOffset? UploadAttemptedAt => TimeEntry?.UploadAttemptedAt;
    public bool CanDelete => !Uploaded;
    public TrackerKey Key => new(RedMinePluginConstants.PluginId, InstanceId);
    public string InstanceId => _settings.InstanceId;
    ViewModelBase ITrackerEditorExtension.View => this;

    private readonly IRedMineDb _database;
    private readonly RedMineInstanceSettings _settings;

    public RedMineEditorRegionViewModel(
        IRedMineUiData data,
        IRedMineApi api,
        IRedMineDb database,
        RedMineInstanceSettings settings)
    {
        _data = data;
        _api = api;
        _database = database;
        _settings = settings;
    }

    public void Load(WorkItem? item, object? binding = null)
    {
        TimeEntry = item is { Id: > 0 }
            ? binding as WorkTimeEntry ?? _database.WorkItemGetTimeEntry(item)
            : null;
        RefreshOptions();
        SyncFromEntry();
    }

    public bool Save(WorkItem item)
    {
        if (item.Id <= 0 || IssueIndex < 0 || ActivityIndex < 0)
            return true;
        TimeEntry = _database.CreateWorkTimeEntry(
            item.Id, RedMineActivities[ActivityIndex].Id, RedMineIssues[IssueIndex].Id);
        return TimeEntry is not null;
    }

    public void CloneTo(ITrackerEditorExtension? target)
    {
        if (target is not RedMineEditorRegionViewModel redmine)
            return;
        redmine.IssueIndex = IssueIndex;
        redmine.IssueText = IssueText;
        redmine.ActivityIndex = ActivityIndex;
    }

    public async Task<TrackerOperationResult> UploadAsync(WorkItem item)
    {
        if (Uploaded)
            return new TrackerOperationResult(false);
        if (IssueIndex < 0 || ActivityIndex < 0 || item.Time <= 0)
            return new TrackerOperationResult(false, "问题或活动不正确，又或者耗时是0");
        if (RedMineIssues[IssueIndex].Disabled || RedMineIssues[IssueIndex].Invalid
            || RedMineActivities[ActivityIndex].Invalid)
            return new TrackerOperationResult(false, "问题或活动已失效");
        if (TimeEntry is null)
            return new TrackerOperationResult(false, "请先保存 Tracker 绑定。", state: TrackerUploadState.Failed);

        if (!SaveUploadState(TrackerUploadState.Pending))
            return new(false, "无法保存 RedMine 同步状态。", state: TrackerUploadState.Uncertain);

        try
        {
            var success = await Task.Run(() =>
                _api.CreateTimeEntry(out var entry, TimeEntry.IssueId,
                    TimeEntry.ActivityId, item.CreateDate, item.Time, item.Comment)
                    ? entry
                    : null);
            if (success is null)
            {
                const string error = "可能是网络问题";
                SaveUploadState(TrackerUploadState.Failed, error);
                return new TrackerOperationResult(false, error);
            }

            TimeEntry.EntryId = success.Id;
            Uploaded = SaveUploadState(TrackerUploadState.Succeeded, remoteId: success.Id.ToString());
            return Uploaded
                ? new TrackerOperationResult(true, remoteId: success.Id.ToString())
                : new TrackerOperationResult(false, "RedMine 工时已提交，但本地状态保存失败。",
                    success.Id.ToString(), TrackerUploadState.Uncertain);
        }
        catch (Exception exception)
        {
            SaveUploadState(TrackerUploadState.Uncertain, exception.Message);
            return new TrackerOperationResult(false, "RedMine 请求结果不确定，请先查询或重试。",
                state: TrackerUploadState.Uncertain);
        }
    }

    private bool SaveUploadState(TrackerUploadState state, string? error = null, string? remoteId = null)
    {
        if (TimeEntry is null)
            return false;
        TimeEntry.UploadState = state;
        TimeEntry.UploadError = error;
        TimeEntry.UploadAttemptedAt ??= DateTimeOffset.UtcNow;
        if (remoteId is not null && int.TryParse(remoteId, out var entryId))
            TimeEntry.EntryId = entryId;
        return _database.UpdateWorkTimeEntry(TimeEntry);
    }

    public TrackerTagDefaultsResult ApplyTagDefaults(WorkTag tag)
    {
        var changed = new List<string>();
        var currentActivityId = ActivityIndex >= 0 ? RedMineActivities[ActivityIndex].Id : (int?)null;
        var currentIssueId = IssueIndex >= 0 ? RedMineIssues[IssueIndex].Id : (int?)null;
        var defaults = RedMineTagDefaults.Apply(
            _settings.TagRules,
            tag.Id,
            currentActivityId,
            currentIssueId,
            RedMineActivities.Where(activity => !activity.Invalid).Select(activity => activity.Id).ToHashSet(),
            RedMineIssues.Where(issue => !issue.Disabled && !issue.Invalid).Select(issue => issue.Id).ToHashSet());
        if (currentActivityId is null && defaults.ActivityId is not null)
        {
            ActivityIndex = Enumerable.Range(0, RedMineActivities.Count)
                .First(i => RedMineActivities[i].Id == defaults.ActivityId);
            changed.Add(nameof(ActivityIndex));
        }
        if (currentIssueId is null && defaults.IssueId is not null)
        {
            IssueIndex = Enumerable.Range(0, RedMineIssues.Count)
                .First(i => RedMineIssues[i].Id == defaults.IssueId);
            IssueText = $"#{RedMineIssues[IssueIndex].Id} {RedMineIssues[IssueIndex].Title} ({RedMineIssues[IssueIndex].Project})";
            changed.Add(nameof(IssueIndex));
        }
        return new TrackerTagDefaultsResult(
            changed,
            defaults.Conflicts.Select(conflict => new TrackerTagDefaultConflict(
                conflict.Field,
                conflict.RuleIds)).ToArray(),
            defaults.InvalidTargets.Select(target => new TrackerTagDefaultInvalidTarget(
                target.Field,
                target.TargetId.ToString(),
                target.RuleId)).ToArray());
    }

    private void SyncFromEntry()
    {
        if (TimeEntry is null)
        {
            IssueIndex = ActivityIndex = -1;
            IssueText = string.Empty;
            Uploaded = false;
            return;
        }

        IssueIndex = Enumerable.Range(0, RedMineIssues.Count)
            .FirstOrDefault(i => RedMineIssues[i].Id == TimeEntry.IssueId, -1);
        ActivityIndex = Enumerable.Range(0, RedMineActivities.Count)
            .FirstOrDefault(i => RedMineActivities[i].Id == TimeEntry.ActivityId, -1);
        IssueText = IssueIndex >= 0
            ? $"#{RedMineIssues[IssueIndex].Id} {RedMineIssues[IssueIndex].Title} ({RedMineIssues[IssueIndex].Project})"
            : string.Empty;
        Uploaded = TimeEntry.EntryId > 0;
    }

    private void RefreshOptions()
    {
        IReadOnlyList<RedMineActivity> activities = _data.RedMineActivities;
        IReadOnlyList<RedMineIssueDisplay> issues = _data.RedMineIssuesOpen;
        if (TimeEntry is not null)
        {
            if (activities.All(activity => activity.Id != TimeEntry.ActivityId))
            {
                activities =
                [
                    .. activities,
                    new RedMineActivity
                    {
                        Id = TimeEntry.ActivityId,
                        Title = $"活动 #{TimeEntry.ActivityId}",
                        Invalid = true,
                    },
                ];
            }
            if (issues.All(issue => issue.Id != TimeEntry.IssueId))
            {
                var stored = _data.RedMineIssues.FirstOrDefault(issue => issue.Id == TimeEntry.IssueId);
                issues =
                [
                    .. issues,
                    stored is null
                        ? new RedMineIssueDisplay
                        {
                            Id = TimeEntry.IssueId,
                            Title = $"问题 #{TimeEntry.IssueId}",
                            AssignedTo = string.Empty,
                            Project = string.Empty,
                            Invalid = true,
                        }
                        : new RedMineIssueDisplay
                        {
                            Id = stored.Id,
                            Title = stored.Title,
                            AssignedTo = stored.AssignedTo,
                            Project = stored.Project,
                            Disabled = stored.Disabled,
                            Invalid = true,
                        },
                ];
            }
        }
        RedMineActivities = activities;
        RedMineIssues = issues;
    }
}
