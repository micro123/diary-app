using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.Utils;

namespace Diary.App.ViewModels;

public sealed partial class WorkItemQueryTag(WorkTag tag) : ObservableObject
{
    public WorkTag Tag { get; } = tag;
    [ObservableProperty] private bool _selected;
}

public sealed record WorkItemQueryResult(WorkItem Item, string Tags)
{
    public string Date => Item.CreateDate;
    public string Comment => Item.Comment;
    public double Time => Item.Time;
    public WorkPriorities Priority => Item.Priority;
}

[DiAutoRegister(singleton: true)]
public sealed partial class WorkItemQueryViewModel : ViewModelBase
{
    private readonly DbShareData _shareData;

    [ObservableProperty] private DateTime _startDate;
    [ObservableProperty] private DateTime _endDate;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private int _tagFilterIndex;
    [ObservableProperty] private int _priorityIndex;
    [ObservableProperty] private string _resultSummary = "尚未查询";

    public ObservableCollection<WorkItemQueryTag> Tags { get; } = new();
    public ObservableCollection<WorkItemQueryResult> Results { get; } = new();
    public IReadOnlyList<string> TagFilters { get; } = ["忽略标签", "任意标签", "全部标签", "无标签"];
    public IReadOnlyList<string> Priorities { get; } = ["全部优先级", .. Enum.GetNames<WorkPriorities>()];

    public WorkItemQueryViewModel(DbShareData shareData)
    {
        _shareData = shareData;
        StartDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        EndDate = StartDate.AddMonths(1).AddDays(-1);
        ReloadTags();
    }

    public override void OnShow() => ReloadTags();

    [RelayCommand]
    private void Query()
    {
        Results.Clear();
        if (StartDate > EndDate)
        {
            ResultSummary = "开始日期不能晚于结束日期";
            return;
        }

        var db = App.Instance.UseDb;
        if (db is null)
        {
            ResultSummary = "数据库不可用";
            return;
        }

        var filter = (WorkItemTagFilter)TagFilterIndex;
        var selectedTags = Tags.Where(tag => tag.Selected).Select(tag => tag.Tag.Id).ToArray();
        if (filter is WorkItemTagFilter.Any or WorkItemTagFilter.All && selectedTags.Length == 0)
        {
            ResultSummary = "请至少选择一个标签";
            return;
        }

        var items = db.QueryWorkItems(new WorkItemQuery
        {
            StartDate = TimeTools.FormatDateTime(StartDate),
            EndDate = TimeTools.FormatDateTime(EndDate),
            Text = string.IsNullOrWhiteSpace(Text) ? null : Text.Trim(),
            TagIds = selectedTags,
            TagFilter = filter,
            Priority = PriorityIndex > 0 ? (WorkPriorities)(PriorityIndex - 1) : null,
        });
        foreach (var item in items)
        {
            var tags = string.Join("、", db.GetWorkItemTags(item).Select(tag => tag.Name));
            Results.Add(new WorkItemQueryResult(item, tags));
        }
        ResultSummary = $"共找到 {Results.Count} 项";
    }

    private void ReloadTags()
    {
        var selectedIds = Tags.Where(tag => tag.Selected).Select(tag => tag.Tag.Id).ToHashSet();
        Tags.Clear();
        foreach (var tag in _shareData.WorkTags.Where(tag => !tag.Disabled))
            Tags.Add(new WorkItemQueryTag(tag) { Selected = selectedIds.Contains(tag.Id) });
    }
}
