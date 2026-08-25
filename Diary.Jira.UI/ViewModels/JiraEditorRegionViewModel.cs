using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;

namespace Diary.Jira.UI.ViewModels;

public partial class JiraEditorRegionViewModel : ViewModelBase, ITrackerEditorExtension
{
    private readonly JiraUiDataStore _data;
    private readonly IJiraApi _api;
    private readonly IJiraDb _database;
    private JiraWorkTimeEntry? _timeEntry;
    private JiraInstanceSettings _settings;

    [ObservableProperty]
    private IReadOnlyList<JiraIssueDisplay> _issues = Array.Empty<JiraIssueDisplay>();
    [ObservableProperty] private int _issueIndex = -1;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _uploaded;
    [ObservableProperty] private string _issueText = string.Empty;
    [ObservableProperty] private bool _refreshing;

    public JiraEditorRegionViewModel(JiraUiDataStore data, IJiraApi api, IJiraDb database, JiraInstanceSettings settings)
    {
        _data = data;
        _api = api;
        _database = database;
        _settings = settings;
    }

    public TrackerKey Key => new(JiraPluginConstants.PluginId, InstanceId);
    public string InstanceId => _settings.InstanceId;
    public bool IsLocked => Uploaded;
    public TrackerUploadState UploadState => _timeEntry?.UploadState ?? TrackerUploadState.NotAttempted;
    public string? UploadError => _timeEntry?.UploadError;
    public DateTimeOffset? UploadAttemptedAt => _timeEntry?.UploadAttemptedAt;
    public bool CanDelete => !Uploaded;
    public bool HasChanges => SelectedIssueKey != _timeEntry?.IssueKey;
    ViewModelBase ITrackerEditorExtension.View => this;

    private string? SelectedIssueKey => IssueIndex >= 0 && IssueIndex < Issues.Count
        ? Issues[IssueIndex].Key
        : null;

    public void Load(WorkItem? item, object? binding = null)
        => LoadCore(item, binding, queryWhenMissing: true);

    public void LoadFromBatch(WorkItem? item, object? binding)
        => LoadCore(item, binding, queryWhenMissing: false);

    private void LoadCore(WorkItem? item, object? binding, bool queryWhenMissing)
    {
        _timeEntry = item is { Id: > 0 }
            ? binding as JiraWorkTimeEntry ?? (queryWhenMissing ? _database.WorkItemGetTimeEntry(item) : null)
            : null;
        ReloadIssues();
        SyncFromEntry();
    }

    public bool Save(WorkItem item)
    {
        if (item.Id <= 0 || IssueIndex < 0 || IssueIndex >= Issues.Count) return true;
        _timeEntry = _database.CreateWorkTimeEntry(item.Id, Issues[IssueIndex].Key);
        return _timeEntry is not null;
    }

    public void CloneTo(ITrackerEditorExtension? target)
    {
        if (target is not JiraEditorRegionViewModel jira) return;
        jira.IssueIndex = IssueIndex;
        jira.IssueText = IssueText;
    }

    public TrackerUploadValidation ValidateUpload(WorkItem item)
    {
        if (item.Time <= 0)
            return TrackerUploadValidation.Invalid("耗时必须大于 0");
        if (_timeEntry is null || string.IsNullOrWhiteSpace(_timeEntry.IssueKey))
            return TrackerUploadValidation.Invalid("未设置 Jira Issue");
        if (IssueIndex < 0 || IssueIndex >= Issues.Count)
            return TrackerUploadValidation.Invalid("Jira Issue 不存在或已失效");
        return TrackerUploadValidation.Valid;
    }

    public async Task<TrackerOperationResult> UploadAsync(WorkItem item)
    {
        if (Uploaded) return new TrackerOperationResult(false);
        if (_timeEntry is null || string.IsNullOrWhiteSpace(_timeEntry.IssueKey)) return new(false, "请先选择 Jira Issue。");
        if (item.Time <= 0) return new(false, "耗时必须大于 0。");

        if (!SaveUploadState(TrackerUploadState.Pending))
            return new(false, "无法保存 Jira 同步状态。", state: TrackerUploadState.Uncertain);

        var result = await _api.AddWorklogAsync(_timeEntry.IssueKey, DateOnly.Parse(item.CreateDate), item.Time, item.Comment);
        if (!result.Success || result.Value is null)
        {
            var error = result.Error ?? "Jira 工时提交失败。";
            SaveUploadState(TrackerUploadState.Failed, error);
            return new(false, error);
        }

        _timeEntry.RemoteWorklogId = result.Value.Id;
        Uploaded = SaveUploadState(TrackerUploadState.Succeeded, remoteId: result.Value.Id);
        return Uploaded
            ? new(true, remoteId: result.Value.Id)
            : new(false, "Jira 工时已提交，但本地状态保存失败。", result.Value.Id, TrackerUploadState.Uncertain);
    }

    private bool SaveUploadState(TrackerUploadState state, string? error = null, string? remoteId = null)
    {
        if (_timeEntry is null)
            return false;
        _timeEntry.UploadState = state;
        _timeEntry.UploadError = error;
        _timeEntry.UploadAttemptedAt ??= DateTimeOffset.UtcNow;
        if (remoteId is not null)
            _timeEntry.RemoteWorklogId = remoteId;
        return _database.UpdateWorkTimeEntry(_timeEntry);
    }

    [RelayCommand]
    private async Task RefreshIssuesAsync()
    {
        if (Refreshing) return;
        try
        {
            Refreshing = true;
            await _data.RefreshAsync(_api);
            ReloadIssues();
            SyncFromEntry();
        }
        catch
        {
            // 编辑器不应因远程刷新失败阻止本地记录。
        }
        finally
        {
            Refreshing = false;
        }
    }

    private void ReloadIssues()
        => Issues = _data.IssuesOpen;

    private void SyncFromEntry()
    {
        Uploaded = _timeEntry?.Uploaded == true;
        IssueIndex = _timeEntry is null ? -1 : Enumerable.Range(0, Issues.Count).FirstOrDefault(index => Issues[index].Key == _timeEntry.IssueKey, -1);
        IssueText = IssueIndex >= 0 ? Issues[IssueIndex].DisplayTitle : _timeEntry?.IssueKey ?? string.Empty;
    }
}
