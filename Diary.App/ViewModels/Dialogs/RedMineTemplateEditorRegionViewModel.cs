using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Diary.App.Models;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;
using Diary.GUIBase.ViewModels;

namespace Diary.App.ViewModels.Dialogs;

/// <summary>
/// RedMine 模板扩展编辑区：默认活动/默认问题 ComboBox。由 RedMineTemplateContributor.CreateEditor 创建，
/// 经 ViewLocator 渲染 RedMineTemplateEditorRegionView。承载 <see cref="RedMineTemplateData"/> 的 UI 编辑。
/// </summary>
public partial class RedMineTemplateEditorRegionViewModel : ViewModelBase
{
    private readonly DbShareData _shareData;

    [ObservableProperty] private int _activityIndex = -1;
    [ObservableProperty] private int _issueIndex = -1;

    /// <summary>本区所属 tracker 插件标识（由 contributor 设置，供协调器 SaveEditors 匹配）。</summary>
    public string PluginId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;

    public ObservableCollection<RedMineActivity> Activities => _shareData.RedMineActivities;
    public ObservableCollection<RedMineIssueDisplay> Issues => _shareData.RedMineIssuesOpen;

    /// <summary>当前选中活动的稳定 id（-1 表示未选）。</summary>
    public int ActivityId => ActivityIndex >= 0 && ActivityIndex < Activities.Count
        ? Activities[ActivityIndex].Id
        : -1;

    /// <summary>当前选中问题的稳定 id（-1 表示未选）。</summary>
    public int IssueId => IssueIndex >= 0 && IssueIndex < Issues.Count
        ? Issues[IssueIndex].Id
        : -1;

    public RedMineTemplateEditorRegionViewModel(RedMineTemplateData data, DbShareData shareData)
    {
        _shareData = shareData;
        // 按 id 反查 ComboBox index
        for (var i = 0; i < Activities.Count; i++)
            if (Activities[i].Id == data.ActivityId) { ActivityIndex = i; break; }
        for (var i = 0; i < Issues.Count; i++)
            if (Issues[i].Id == data.IssueId) { IssueIndex = i; break; }
    }

    /// <summary>从当前 UI 选择导出稳定数据。</summary>
    public RedMineTemplateData ToData() => new() { ActivityId = ActivityId, IssueId = IssueId };
}
