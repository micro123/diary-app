using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.GUIBase.ViewModels;
using Diary.RedMine.Models;

namespace Diary.RedMine.UI.ViewModels.Dialogs;

public partial class RedMineTemplateEditorRegionViewModel : ViewModelBase
{
    private readonly IRedMineUiData _data;

    [ObservableProperty] private int _activityIndex = -1;
    [ObservableProperty] private int _issueIndex = -1;

    public string PluginId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public ObservableCollection<RedMineActivity> Activities => _data.RedMineActivities;
    public ObservableCollection<RedMineIssueDisplay> Issues => _data.RedMineIssuesOpen;
    public int ActivityId => ActivityIndex >= 0 && ActivityIndex < Activities.Count ? Activities[ActivityIndex].Id : -1;
    public int IssueId => IssueIndex >= 0 && IssueIndex < Issues.Count ? Issues[IssueIndex].Id : -1;

    public RedMineTemplateEditorRegionViewModel(RedMineTemplateData data, IRedMineUiData uiData)
    {
        _data = uiData;
        for (var i = 0; i < Activities.Count; i++)
            if (Activities[i].Id == data.ActivityId) ActivityIndex = i;
        for (var i = 0; i < Issues.Count; i++)
            if (Issues[i].Id == data.IssueId) IssueIndex = i;
    }

    public RedMineTemplateData ToData() => new() { ActivityId = ActivityId, IssueId = IssueId };
}
