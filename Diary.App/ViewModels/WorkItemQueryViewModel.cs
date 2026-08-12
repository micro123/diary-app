using System.Collections.ObjectModel;
using System.Text;
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

public sealed record WorkItemQueryResult(WorkItem Item, string Tags, string PrimaryTag = "")
{
    public string Date => Item.CreateDate;
    public string Comment => Item.Comment;
    public double Time => Item.Time;
    public WorkPriorities Priority => Item.Priority;
    public string GroupLabel => string.IsNullOrWhiteSpace(PrimaryTag) ? "未分类" : PrimaryTag;
}

[DiAutoRegister(singleton: true)]
public sealed partial class WorkItemQueryViewModel : ViewModelBase
{
    internal const int DefaultResultLimit = 200;

    private readonly DbShareData _shareData;
    private readonly ILogger _logger;
    private readonly SavedWorkItemQueryStore _savedQueryStore;
    private readonly Func<DbInterfaceBase?> _databaseProvider;
    private readonly Func<string, string, Task<bool>> _confirmDelete;

    [ObservableProperty] private DateTime _startDate;
    [ObservableProperty] private DateTime _endDate;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private int _tagFilterIndex;
    [ObservableProperty] private int _priorityIndex;
    [ObservableProperty] private string _resultSummary = "尚未查询";
    [ObservableProperty] private string _resultBreakdown = string.Empty;
    public bool HasResultBreakdown => !string.IsNullOrWhiteSpace(ResultBreakdown);
    public string QuerySummaryText => string.Join(
        Environment.NewLine,
        "查询汇总",
        $"记录数：{Results.Count}",
        $"总工时：{ResultTotalHours:0.##} 小时",
        ResultBreakdown);
    [ObservableProperty] private double _resultTotalHours;
    [ObservableProperty] private bool _hasQueryError;
    [ObservableProperty] private string _savedQueryName = string.Empty;
    [ObservableProperty] private string _savedQueryStatus = string.Empty;
    [ObservableProperty] private SavedWorkItemQuery? _selectedSavedQuery;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCsvCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportMarkdownCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyBreakdownCommand))]
    private ObservableCollection<WorkItemQueryResult> _results = new();

    public ObservableCollection<WorkItemQueryTag> Tags { get; } = new();
    public ObservableCollection<SavedWorkItemQuery> SavedQueries { get; } = new();
    public IReadOnlyList<string> TagFilters { get; } = ["忽略标签", "任意标签", "全部标签", "无标签", "精确匹配"];
    public IReadOnlyList<string> Priorities { get; } = ["全部优先级", .. Enum.GetNames<WorkPriorities>()];

    [RelayCommand]
    private void QuickSelectDate(string which)
    {
        var part = (AdjustPart)(which[2] - '0');
        var direction = (AdjustDirection)(which[1] - '0');
        var start = StartDate;
        var end = EndDate;
        TimeTools.AdjustDate(ref start, ref end, part, direction);
        StartDate = start;
        EndDate = end;
    }

    [RelayCommand]
    private void SelectToday() => SetSingleDay(DateTime.Today);

    [RelayCommand]
    private void SelectYesterday() => SetSingleDay(DateTime.Today.AddDays(-1));

    [RelayCommand]
    private void SelectCurrentWeek() => SetQuickRange(AdjustPart.Week);

    [RelayCommand]
    private void SelectCurrentMonth() => SetQuickRange(AdjustPart.Month);

    private void SetSingleDay(DateTime date)
    {
        StartDate = date.Date;
        EndDate = date.Date;
    }

    private void SetQuickRange(AdjustPart part)
    {
        var start = DateTime.Today;
        var end = start;
        TimeTools.AdjustDate(ref start, ref end, part, AdjustDirection.Current);
        StartDate = start;
        EndDate = end;
    }

    public WorkItemQueryViewModel(DbShareData shareData, ILogger logger)
        : this(
            shareData,
            logger,
            new SavedWorkItemQueryStore(availableTags: shareData.WorkTags),
            () => App.Instance.UseDb,
            EventDispatcher.Confirm)
    {
    }

    internal WorkItemQueryViewModel(
        DbShareData shareData,
        ILogger logger,
        SavedWorkItemQueryStore savedQueryStore,
        Func<DbInterfaceBase?> databaseProvider,
        Func<string, string, Task<bool>>? confirmDelete = null)
    {
        _shareData = shareData;
        _logger = logger;
        _savedQueryStore = savedQueryStore;
        _databaseProvider = databaseProvider;
        _confirmDelete = confirmDelete ?? EventDispatcher.Confirm;
        StartDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        EndDate = StartDate.AddMonths(1).AddDays(-1);
        ReloadTags();
        RefreshSavedQueries();
        SavedQueryStatus = _savedQueryStore.LoadWarning;
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
            var nextResults = items.Select(item =>
            {
                var tags = tagsByWorkId.TryGetValue(item.Id, out var itemTags)
                    ? itemTags
                    : Array.Empty<WorkTag>();
                return new WorkItemQueryResult(
                    item,
                    string.Join("、", tags.Select(tag => tag.Name)),
                    tags.FirstOrDefault(tag => tag.Level == TagLevels.Primary)?.Name ?? string.Empty);
            });
            Results = new ObservableCollection<WorkItemQueryResult>(nextResults);
            HasQueryError = false;
            ResultTotalHours = Results.Sum(result => result.Time);
            ResultBreakdown = BuildBreakdown(Results);
            OnPropertyChanged(nameof(HasResultBreakdown));
            OnPropertyChanged(nameof(QuerySummaryText));
            ResultSummary = Results.Count == DefaultResultLimit
                ? $"已显示前 {DefaultResultLimit} 项，结果可能已截断；合计 {ResultTotalHours:0.##} 小时"
                : $"共找到 {Results.Count} 项，合计 {ResultTotalHours:0.##} 小时";
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

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCsv()
    {
        var path = await SaveTextFileAsync("导出查询结果", "diary-query.csv", "csv", BuildCsv());
        if (path is not null)
            NotificationManager?.Show($"查询结果已导出：{path}", Avalonia.Controls.Notifications.NotificationType.Success);
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportMarkdown()
    {
        var path = await SaveTextFileAsync("导出查询结果", "diary-query.md", "md", BuildMarkdown());
        if (path is not null)
            NotificationManager?.Show($"查询结果已导出：{path}", Avalonia.Controls.Notifications.NotificationType.Success);
    }

    [RelayCommand(CanExecute = nameof(CanCopyBreakdown))]
    private async Task CopyBreakdown()
    {
        if (await CopyStringToClipboardAsync(QuerySummaryText))
        {
            NotificationManager?.Show(
                "查询汇总已复制",
                Avalonia.Controls.Notifications.NotificationType.Success);
        }
    }

    private string BuildCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine("日期,事项,耗时(小时),优先级,主标签,标签");
        foreach (var result in Results)
        {
            builder.AppendLine(string.Join(",", EscapeCsv(result.Date), EscapeCsv(result.Comment),
                result.Time.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                EscapeCsv(result.Priority.ToString()), EscapeCsv(result.GroupLabel), EscapeCsv(result.Tags)));
        }
        return builder.ToString();
    }

    private string BuildMarkdown()
    {
        var builder = new StringBuilder();
        builder.AppendLine("| 日期 | 事项 | 耗时（小时） | 优先级 | 主标签 | 标签 |");
        builder.AppendLine("| --- | --- | ---: | --- | --- | --- |");
        foreach (var result in Results)
        {
            builder.AppendLine($"| {EscapeMarkdown(result.Date)} | {EscapeMarkdown(result.Comment)} | {result.Time:0.##} | {result.Priority} | {EscapeMarkdown(result.GroupLabel)} | {EscapeMarkdown(result.Tags)} |");
        }
        builder.AppendLine();
        builder.AppendLine($"合计：{Results.Count} 条，{ResultTotalHours:0.##} 小时");
        return builder.ToString();
    }

    private static string BuildBreakdown(IEnumerable<WorkItemQueryResult> results)
    {
        var materialized = results.ToArray();
        if (materialized.Length == 0)
            return string.Empty;

        static string FormatGroups(IEnumerable<IGrouping<string, WorkItemQueryResult>> groups)
            => string.Join("；", groups
                .OrderByDescending(group => group.Sum(result => result.Time))
                .ThenBy(group => group.Key)
                .Take(4)
                .Select(group => $"{group.Key} {group.Sum(result => result.Time):0.##} 小时"));

        return $"按日期：{FormatGroups(materialized.GroupBy(result => result.Date))}；按主标签：{FormatGroups(materialized.GroupBy(result => result.GroupLabel))}";
    }

    private static string EscapeCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string EscapeMarkdown(string value)
        => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private bool CanExport => Results.Count > 0;

    private bool CanCopyBreakdown => Results.Count > 0;

    [RelayCommand]
    private void AddSavedQuery()
    {
        if (!TryBuildQuery(out var query, out var validationError))
        {
            SavedQueryStatus = validationError;
            return;
        }
        if (!_savedQueryStore.TryAdd(SavedQueryName, query, out var error, _shareData.WorkTags))
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
            var snapshots = SelectedSavedQuery.Tags ?? Array.Empty<SavedWorkItemQueryTag>();
            var selectedIds = snapshots
                .Where(snapshot => Tags.Any(tag => tag.Tag.Id == snapshot.Id
                    && tag.Tag.Level == snapshot.Level
                    && string.Equals(tag.Tag.Name, snapshot.Name, StringComparison.Ordinal)))
                .Select(snapshot => snapshot.Id)
                .ToHashSet();
            foreach (var tag in Tags)
                tag.Selected = selectedIds.Contains(tag.Tag.Id);
            var unmatchedCount = snapshots.Length - selectedIds.Count;
            SavedQueryStatus = unmatchedCount == 0
                ? "已应用查询条件"
                : $"已应用；{unmatchedCount} 个标签缺失或同 ID 名称/层级不一致，未选择";
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
        if (!_savedQueryStore.TryUpdate(id, query, out var error, _shareData.WorkTags))
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
    private async Task DeleteSavedQuery()
    {
        var selected = SelectedSavedQuery;
        if (selected is null)
        {
            SavedQueryStatus = "请先选择保存的查询";
            return;
        }
        if (!await _confirmDelete("删除保存的查询", $"确认删除“{selected.Name}”吗？"))
        {
            SavedQueryStatus = "已取消删除";
            return;
        }
        if (!_savedQueryStore.TryDelete(selected.Id, out var error))
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
        var filter = (WorkItemTagFilter)TagFilterIndex;
        var selectedTags = Tags.Where(tag => tag.Selected).Select(tag => tag.Tag.Id).ToArray();
        var candidate = new WorkItemQuery
        {
            StartDate = TimeTools.FormatDateTime(StartDate),
            EndDate = TimeTools.FormatDateTime(EndDate),
            Text = string.IsNullOrWhiteSpace(Text) ? null : Text.Trim(),
            TagIds = selectedTags,
            TagFilter = filter,
            Priority = PriorityIndex == 0 ? null : (WorkPriorities)(PriorityIndex - 1),
            Limit = DefaultResultLimit,
        };
        return WorkItemQueryNormalizer.TryNormalize(candidate, out query, out error);
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
