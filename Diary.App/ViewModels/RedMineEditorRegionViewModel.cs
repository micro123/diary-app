using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.RedMine.Models;
using Diary.Database;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.RedMine;

namespace Diary.App.ViewModels;

/// <summary>
/// RedMine 编辑器扩展区：issue/activity 选择 + 上传。实现 <see cref="ITrackerEditorExtension"/>，
/// 由编辑器经接口回调。远程调用经 <see cref="IRedMineApi"/>，本地绑定经 <see cref="IRedMineDb"/>。
/// </summary>
public partial class RedMineEditorRegionViewModel : ViewModelBase, ITrackerEditorExtension
{
    private readonly DbShareData _shareData;
    private readonly IRedMineApi _api;
    private WorkTimeEntry? TimeEntry { get; set; }

    public ObservableCollection<RedMineIssueDisplay> RedMineIssues => _shareData.RedMineIssuesOpen;
    public ObservableCollection<RedMineActivity> RedMineActivities => _shareData.RedMineActivities;

    [ObservableProperty] private int _issueIndex = -1;
    [ObservableProperty] private int _activityIndex = -1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _uploaded = false;
    [ObservableProperty] private string _issueText = string.Empty;

    public bool IsLocked => Uploaded;
    public bool CanDelete => !Uploaded;

    // ---- ITrackerEditorExtension ----
    public string InstanceId => "redmine.default";
    // 显式实现：避免与 ViewModelBase.View（Control?，ViewLocator.SetView 设置的附加控件）命名冲突
    ViewModelBase ITrackerEditorExtension.View => this;

    private static IRedMineDb? RedMineDb => App.Instance.UseDb?.GetExtension<IRedMineDb>();

    public RedMineEditorRegionViewModel(DbShareData shareData, IRedMineApi api)
    {
        _shareData = shareData;
        _api = api;
    }

    public void Load(WorkItem? item, object? binding = null)
    {
        TimeEntry = null;
        if (item is { Id: > 0 })
        {
            TimeEntry = binding as WorkTimeEntry ?? RedMineDb?.WorkItemGetTimeEntry(item);
        }

        SyncFromEntry();
    }

    public void Save(WorkItem item)
    {
        if (item.Id <= 0)
            return;
        // 保存 redmine 信息，如果有效的话
        if (IssueIndex >= 0 && ActivityIndex >= 0)
        {
            TimeEntry = RedMineDb?.CreateWorkTimeEntry(item.Id,
                RedMineActivities[ActivityIndex].Id, RedMineIssues[IssueIndex].Id);
        }
    }

    public void CloneTo(ITrackerEditorExtension? target)
    {
        if (target is not RedMineEditorRegionViewModel r)
            return;
        r.IssueIndex = IssueIndex;
        r.IssueText = IssueText;
        r.ActivityIndex = ActivityIndex;
    }

    public async Task<TrackerOperationResult> UploadAsync(WorkItem item)
    {
        if (Uploaded)
            return new TrackerOperationResult(false);
        if (!(IssueIndex >= 0 && ActivityIndex >= 0 && item.Time > 0))
            return new TrackerOperationResult(false, "问题或活动不正确，又或者耗时是0");
        Debug.Assert(TimeEntry is not null);

        // 网络 API 调用与 DB 写入一并放到后台线程，避免在 UI 线程同步写库造成卡顿
        var entryId = 0;
        await Task.Run(() =>
        {
            if (_api.CreateTimeEntry(out var ti, TimeEntry!.IssueId,
                    TimeEntry.ActivityId, item.CreateDate, item.Time, item.Comment))
                entryId = ti!.Id;
            TimeEntry.EntryId = entryId;
            RedMineDb?.UpdateWorkTimeEntry(TimeEntry); // 关联到数据库
        });
        Uploaded = entryId > 0;
        return Uploaded
            ? new TrackerOperationResult(true, RemoteId: entryId.ToString())
            : new TrackerOperationResult(false, "可能是网络问题");
    }

    public void SetActivity(int activityId)
    {
        for (var i = 0; i < RedMineActivities.Count; i++)
        {
            if (RedMineActivities[i].Id == activityId)
            {
                ActivityIndex = i;
                return;
            }
        }

        ActivityIndex = -1;
    }

    public void SetIssue(int issueId)
    {
        for (var i = 0; i < RedMineIssues.Count; i++)
        {
            var x = RedMineIssues[i];
            if (x.Id == issueId)
            {
                IssueIndex = i;
                IssueText = $"#{x.Id} {x.Title} ({x.Project})";
                return;
            }
        }

        IssueIndex = -1;
        IssueText = string.Empty;
    }

    private void SyncFromEntry()
    {
        if (TimeEntry == null)
        {
            IssueIndex = ActivityIndex = -1;
            IssueText = string.Empty;
            Uploaded = false;
            return;
        }

        for (var i = 0; i < RedMineIssues.Count; i++)
        {
            if (TimeEntry.IssueId == RedMineIssues[i].Id)
            {
                IssueIndex = i;
                IssueText = $"#{RedMineIssues[i].Id} {RedMineIssues[i].Title} ({RedMineIssues[i].Project})";
                break;
            }
        }

        for (var i = 0; i < RedMineActivities.Count; i++)
        {
            if (TimeEntry.ActivityId == RedMineActivities[i].Id)
            {
                ActivityIndex = i;
                break;
            }
        }

        Uploaded = TimeEntry.EntryId > 0;
    }
}
