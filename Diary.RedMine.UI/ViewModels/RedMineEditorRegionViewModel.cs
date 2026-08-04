using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.Core.Data.Base;
using Diary.GUIBase;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.RedMine;
using Diary.RedMine.Models;
using Diary.RedMine.UI.ViewModels.Dialogs;

namespace Diary.RedMine.UI.ViewModels;

public partial class RedMineEditorRegionViewModel : ViewModelBase, ITrackerEditorExtension
{
    private readonly IRedMineUiData _data;
    private readonly IRedMineApi _api;
    private WorkTimeEntry? TimeEntry { get; set; }

    public ObservableCollection<RedMineIssueDisplay> RedMineIssues => _data.RedMineIssuesOpen;
    public ObservableCollection<RedMineActivity> RedMineActivities => _data.RedMineActivities;

    [ObservableProperty] private int _issueIndex = -1;
    [ObservableProperty] private int _activityIndex = -1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _uploaded;
    [ObservableProperty] private string _issueText = string.Empty;

    public bool IsLocked => Uploaded;
    public bool CanDelete => !Uploaded;
    public TrackerKey Key => new(RedMinePluginConstants.PluginId, InstanceId);
    public string InstanceId => RedMinePluginConstants.DefaultInstanceId;
    ViewModelBase ITrackerEditorExtension.View => this;

    private static IRedMineDb? RedMineDb => BaseApp.Instance.UseDb?.GetExtension<IRedMineDb>();

    public RedMineEditorRegionViewModel(IRedMineUiData data, IRedMineApi api)
    {
        _data = data;
        _api = api;
    }

    public void Load(WorkItem? item, object? binding = null)
    {
        TimeEntry = item is { Id: > 0 }
            ? binding as WorkTimeEntry ?? RedMineDb?.WorkItemGetTimeEntry(item)
            : null;
        SyncFromEntry();
    }

    public void Save(WorkItem item)
    {
        if (item.Id <= 0 || IssueIndex < 0 || ActivityIndex < 0)
            return;
        TimeEntry = RedMineDb?.CreateWorkTimeEntry(
            item.Id, RedMineActivities[ActivityIndex].Id, RedMineIssues[IssueIndex].Id);
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
        Debug.Assert(TimeEntry is not null);

        var entryId = 0;
        await Task.Run(() =>
        {
            if (_api.CreateTimeEntry(out var entry, TimeEntry!.IssueId,
                    TimeEntry.ActivityId, item.CreateDate, item.Time, item.Comment))
            {
                entryId = entry!.Id;
            }
            TimeEntry.EntryId = entryId;
            RedMineDb?.UpdateWorkTimeEntry(TimeEntry);
        });

        Uploaded = entryId > 0;
        return Uploaded
            ? new TrackerOperationResult(true, RemoteId: entryId.ToString())
            : new TrackerOperationResult(false, "可能是网络问题");
    }

    public void ApplyTemplateData(object data)
    {
        if (data is not RedMineTemplateData template)
            return;
        ActivityIndex = Enumerable.Range(0, RedMineActivities.Count)
            .FirstOrDefault(i => RedMineActivities[i].Id == template.ActivityId, -1);
        IssueIndex = Enumerable.Range(0, RedMineIssues.Count)
            .FirstOrDefault(i => RedMineIssues[i].Id == template.IssueId, -1);
        IssueText = IssueIndex >= 0
            ? $"#{RedMineIssues[IssueIndex].Id} {RedMineIssues[IssueIndex].Title} ({RedMineIssues[IssueIndex].Project})"
            : string.Empty;
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
}
