using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Models;
using Diary.Core.Constants;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.Utils;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger _logger;
    private readonly SavedWorkItemQueryStore _savedQueryStore;
    private readonly Func<DbInterfaceBase?> _databaseProvider;

    [ObservableProperty] private DateTime _startDate;
    [ObservableProperty] private DateTime _endDate;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private int _tagFilterIndex;
    [ObservableProperty] private int _priorityIndex;
    [ObservableProperty] private string _resultSummary = "尚未查询";
    [ObservableProperty] private bool _hasQueryError;
    [ObservableProperty] private string _savedQueryName = string.Empty;
    [ObservableProperty] private string _savedQueryStatus = string.Empty;
    [ObservableProperty] private SavedWorkItemQuery? _selectedSavedQuery;
    [ObservableProperty] private ObservableCollection<WorkItemQueryResult> _results = new();

    public ObservableCollection<WorkItemQueryTag> Tags { get; } = new();
    public ObservableCollection<SavedWorkItemQuery> SavedQueries { get; } = new();
    public IReadOnlyList<string> TagFilters { get; } = ["忽略标签", "任意标签", "全部标签", "无标签", "精确匹配"];
    public IReadOnlyList<string> Priorities { get; } = ["全部优先级", .. Enum.GetNames<WorkPriorities>()];

    public WorkItemQueryViewModel(DbShareData shareData, ILogger logger)
        : this(shareData, logger, new SavedWorkItemQueryStore(), () => App.Instance.UseDb)
    {
    }

    internal WorkItemQueryViewModel(
        DbShareData shareData,
        ILogger logger,
        SavedWorkItemQueryStore savedQueryStore,
        Func<DbInterfaceBase?> databaseProvider)
    {
        _shareData = shareData;
        _logger = logger;
        _savedQueryStore = savedQueryStore;
        _databaseProvider = databaseProvider;
        StartDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        EndDate = StartDate.AddMonths(1).AddDays(-1);
        ReloadTags();
        RefreshSavedQueries();
    }

    public override void OnShow() => ReloadTags();

    [RelayCommand]
    private void Query()
    {
        if (!TryBuildQuery(out var query, out var validationError))
        {
            SetQueryError(validationError);
            return;
        }

        var db = _databaseProvider();
        if (db is null)
        {
            SetQueryError("数据库不可用，已保留上次查询结果");
            return;
        }

        try
        {
            var items = db.QueryWorkItems(query);
            var tagsByWorkId = db.GetWorkTagsByWorkItemIds(items.Select(item => item.Id).ToArray());
            var nextResults = items.Select(item => new WorkItemQueryResult(
                item,
                tagsByWorkId.TryGetValue(item.Id, out var tags)
                    ? string.Join("、", tags.Select(tag => tag.Name))
                    : string.Empty));
            Results = new ObservableCollection<WorkItemQueryResult>(nextResults);
            HasQueryError = false;
            ResultSummary = $"共找到 {Results.Count} 项";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Work item query failed for provider {Provider}", db.ProviderName);
            SetQueryError("查询失败，请稍后重试；已保留上次成功结果");
        }
    }

    [RelayCommand]
    private static void OpenResult(WorkItemQueryResult result)
    {
        EventDispatcher.RouteToPage(PageNames.DiaryEditor);
        EventDispatcher.Msg(new OpenWorkItemEvent(result.Date, result.Item.Id));
    }

    [RelayCommand]
    private void AddSavedQuery()
    {
        if (!TryBuildQuery(out var query, out var validationError))
        {
            SavedQueryStatus = validationError;
            return;
        }
        if (!_savedQueryStore.TryAdd(SavedQueryName, query, out var error))
        {
            SavedQueryStatus = error;
            return;
        }
        var added = _savedQueryStore.Queries[^1];
        RefreshSavedQueries(added.Id);
        SavedQueryStatus = "查询已保存";
    }

    [RelayCommand]
    private void ApplySavedQuery()
    {
        if (SelectedSavedQuery is null)
        {
            SavedQueryStatus = "请先选择保存的查询";
            return;
        }

        try
        {
            var query = SelectedSavedQuery.ToQuery();
            if (query.StartDate is not null)
                StartDate = TimeTools.FromFormatedDate(query.StartDate);
            if (query.EndDate is not null)
                EndDate = TimeTools.FromFormatedDate(query.EndDate);
            Text = query.Text ?? string.Empty;
            TagFilterIndex = (int)query.TagFilter;
            PriorityIndex = query.Priority is null ? 0 : (int)query.Priority.Value + 1;
            ReloadTags();
            var availableIds = Tags.Select(tag => tag.Tag.Id).ToHashSet();
            var selectedIds = query.TagIds.ToHashSet();
            foreach (var tag in Tags)
                tag.Selected = selectedIds.Contains(tag.Tag.Id);
            var unavailableCount = selectedIds.Count(id => !availableIds.Contains(id));
            SavedQueryStatus = unavailableCount == 0
                ? "已应用查询条件"
                : $"已应用；{unavailableCount} 个已删除或禁用的标签被忽略";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid saved work item query {SavedQueryId}", SelectedSavedQuery.Id);
            SavedQueryStatus = "保存的查询条件无效";
        }
    }

    [RelayCommand]
    private void UpdateSavedQuery()
    {
        if (SelectedSavedQuery is null)
        {
            SavedQueryStatus = "请先选择保存的查询";
            return;
        }
        if (!TryBuildQuery(out var query, out var validationError))
        {
            SavedQueryStatus = validationError;
            return;
        }
        var id = SelectedSavedQuery.Id;
        if (!_savedQueryStore.TryUpdate(id, query, out var error))
        {
            SavedQueryStatus = error;
            return;
        }
        RefreshSavedQueries(id);
        SavedQueryStatus = "查询条件已更新";
    }

    [RelayCommand]
    private void RenameSavedQuery()
    {
        if (SelectedSavedQuery is null)
        {
            SavedQueryStatus = "请先选择保存的查询";
            return;
        }
        var id = SelectedSavedQuery.Id;
        if (!_savedQueryStore.TryRename(id, SavedQueryName, out var error))
        {
            SavedQueryStatus = error;
            return;
        }
        RefreshSavedQueries(id);
        SavedQueryStatus = "查询已重命名";
    }

    [RelayCommand]
    private void DeleteSavedQuery()
    {
        if (SelectedSavedQuery is null)
        {
            SavedQueryStatus = "请先选择保存的查询";
            return;
        }
        if (!_savedQueryStore.TryDelete(SelectedSavedQuery.Id, out var error))
        {
            SavedQueryStatus = error;
            return;
        }
        RefreshSavedQueries();
        SavedQueryStatus = "查询已删除";
    }

    partial void OnSelectedSavedQueryChanged(SavedWorkItemQuery? value)
    {
        if (value is not null)
            SavedQueryName = value.Name;
    }

    private bool TryBuildQuery(out WorkItemQuery query, out string error)
    {
        query = new WorkItemQuery();
        if (StartDate > EndDate)
        {
            error = "开始日期不能晚于结束日期";
            return false;
        }
        var filter = (WorkItemTagFilter)TagFilterIndex;
        var selectedTags = Tags.Where(tag => tag.Selected).Select(tag => tag.Tag.Id).ToArray();
        if (filter is WorkItemTagFilter.Any or WorkItemTagFilter.All && selectedTags.Length == 0)
        {
            error = "请至少选择一个标签";
            return false;
        }
        query = new WorkItemQuery
        {
            StartDate = TimeTools.FormatDateTime(StartDate),
            EndDate = TimeTools.FormatDateTime(EndDate),
            Text = string.IsNullOrWhiteSpace(Text) ? null : Text.Trim(),
            TagIds = selectedTags,
            TagFilter = filter,
            Priority = PriorityIndex > 0 ? (WorkPriorities)(PriorityIndex - 1) : null,
        };
        error = string.Empty;
        return true;
    }

    private void SetQueryError(string message)
    {
        HasQueryError = true;
        ResultSummary = message;
    }

    private void RefreshSavedQueries(Guid? selectedId = null)
    {
        SavedQueries.Clear();
        foreach (var saved in _savedQueryStore.Queries)
            SavedQueries.Add(saved);
        SelectedSavedQuery = selectedId is null
            ? null
            : SavedQueries.FirstOrDefault(saved => saved.Id == selectedId);
        if (SelectedSavedQuery is null)
            SavedQueryName = string.Empty;
    }

    private void ReloadTags()
    {
        var selectedIds = Tags.Where(tag => tag.Selected).Select(tag => tag.Tag.Id).ToHashSet();
        Tags.Clear();
        foreach (var tag in _shareData.WorkTags.Where(tag => !tag.Disabled))
            Tags.Add(new WorkItemQueryTag(tag) { Selected = selectedIds.Contains(tag.Id) });
    }
}
