using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.App.Utils;
using Diary.Core.Data.Base;
using Diary.Core.Data.Statistics;
using Diary.Database;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.Utils;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Avalonia;

namespace Diary.App.Models;

public enum StatisticsType
{
    LastWeek,
    LastMonth,
    LastQuarter,
    LastYear,
    ThisWeek,
    ThisMonth,
    ThisQuarter,
    ThisYear,
    Custom,
}

public partial class StatisticsTabData : ObservableObject
{
    private sealed record StatisticsSnapshot(
        double Total,
        IReadOnlyList<double> Times,
        IList<string> Labels,
        IReadOnlyList<StatisticsTimeNode> Details);

    private readonly Func<string, string, StatisticsResult>? _statisticsProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private int _refreshGeneration;

    public StatisticsType Type { get; private set; }

    private static readonly string[] Names =
    [
        "上周",
        "上个月",
        "上季度",
        "去年",
        "这周",
        "这个月",
        "这季度",
        "今年",
        "自定义"
    ];

    public static string GetTypeName(StatisticsType statisticType) => Names[(int)statisticType];
    public string Name { get; init; }
    public bool IsCustom { get; init; }
    public string Icon => !IsCustom ? "fa-calendar-check" : "fa-calendar";

    private DbInterfaceBase? Db => App.Instance.UseDb;

    [ObservableProperty] private DateTime _dateBegin = DateTime.Now.Date;
    [ObservableProperty] private DateTime _dateEnd = DateTime.Now.Date;
    [ObservableProperty] private bool _useCustomTime = false;
    [ObservableProperty] private double _customTotal = 0;
    [ObservableProperty] private double _statisticsTotal = 0;
    [ObservableProperty] private bool _isPieChart;
    [ObservableProperty] private bool _isInitialized;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private HierarchicalTreeDataGridSource<StatisticsTimeNode>? _timeDetails;
    [ObservableProperty] private CartesianChart? _chart;
    [ObservableProperty] private PieChart? _pieChart;

    private ColumnSeries<double>? _bar;
    private Axis? _xAxis;

    /// <inheritdoc/>
    public StatisticsTabData(StatisticsType type)
        : this(type, null, loadImmediately: false)
    {
    }

    internal StatisticsTabData(
        StatisticsType type,
        Func<string, string, StatisticsResult>? statisticsProvider,
        bool loadImmediately)
    {
        Type = type;
        Name = GetTypeName(Type);
        IsCustom = type == StatisticsType.Custom;
        _statisticsProvider = statisticsProvider;
        if (loadImmediately)
            LoadInitialData();
    }

    private void EnsureVisuals()
    {
        if (TimeDetails is not null)
            return;

        TimeDetails = new HierarchicalTreeDataGridSource<StatisticsTimeNode>([])
        {
            Columns =
            {
                new HierarchicalExpanderColumn<StatisticsTimeNode>(
                    new TemplateColumn<StatisticsTimeNode>(
                        "#ID",
                        "IdCell",
                        width: new GridLength(120, GridUnitType.Pixel) ,
                        options: new() { CanUserResizeColumn = false, CanUserSortColumn = false, BeginEditGestures = BeginEditGestures.None }
                    ),
                    x=>x.Children,
                    x => x.Children.Count > 0),
                new TemplateColumn<StatisticsTimeNode>(
                    "标签",
                    "NameCell",
                    width: new GridLength(1, GridUnitType.Star),
                    options: new TemplateColumnOptions<StatisticsTimeNode> { CanUserResizeColumn = false, CanUserSortColumn = false, BeginEditGestures = BeginEditGestures.None }
                    ),
                new TemplateColumn<StatisticsTimeNode>(
                    "耗时",
                    "TimeCell",
                    width: new GridLength(120, GridUnitType.Pixel),
                    options: new TemplateColumnOptions<StatisticsTimeNode> { CanUserResizeColumn = false, CanUserSortColumn = false, BeginEditGestures = BeginEditGestures.None }
                ),
                new TemplateColumn<StatisticsTimeNode>(
                    "占比",
                    "PercentCell",
                    width: new GridLength(120, GridUnitType.Pixel),
                    options: new TemplateColumnOptions<StatisticsTimeNode> { CanUserResizeColumn = false, CanUserSortColumn = false, BeginEditGestures = BeginEditGestures.None }
                ),
                new TemplateColumn<StatisticsTimeNode>(
                    "操作",
                    "OperationsCell",
                    options: new() { CanUserResizeColumn = false, CanUserSortColumn = false, BeginEditGestures = BeginEditGestures.None }
                ),
            }
        };

        _bar = new ColumnSeries<double> { Name = "工时" };
        _xAxis = new Axis { Name = "项目" };
        Chart = new CartesianChart();
        PieChart = new PieChart();
        Chart.Series = [Bar];
        Chart.XAxes =
        [
            XAxis
        ];
        Chart.LegendPosition = LegendPosition.Hidden;
        Chart.ZoomMode = ZoomAndPanMode.None;
        Chart.EasingFunction = null; // disable animations

        PieChart.LegendPosition = LegendPosition.Right;
        PieChart.EasingFunction = null; // disable animations
    }

    private ColumnSeries<double> Bar => _bar!;
    private Axis XAxis => _xAxis!;

    [RelayCommand]
    private Task Refresh()
    {
        return RefreshSafelyAsync();
    }

    internal async Task EnsureInitializedAsync()
    {
        if (IsInitialized)
            return;

        await _initializationLock.WaitAsync();
        try
        {
            if (!IsInitialized)
                await RefreshAsync();
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    internal async Task RefreshAsync()
    {
        IsLoading = true;
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var (begin, end, customTotal) = PrepareRefreshRequest();
        StatisticsSnapshot? snapshot;
        try
        {
            await _refreshLock.WaitAsync();
            try
            {
                snapshot = await Task.Run(() => FetchSnapshot(begin, end, customTotal));
            }
            finally
            {
                _refreshLock.Release();
            }

            if (snapshot is null || generation != Volatile.Read(ref _refreshGeneration))
                return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != Volatile.Read(ref _refreshGeneration))
                    return;
                EnsureVisuals();
                ApplySnapshot(snapshot);
                IsInitialized = true;
            });
        }
        finally
        {
            if (generation == Volatile.Read(ref _refreshGeneration))
                IsLoading = false;
        }
    }

    private async Task RefreshSafelyAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            EventDispatcher.ShowToast($"刷新统计失败：{exception.Message}");
        }
    }

    private void LoadInitialData()
    {
        EnsureVisuals();
        var (begin, end, customTotal) = PrepareRefreshRequest();
        var snapshot = FetchSnapshot(begin, end, customTotal);
        if (snapshot is not null)
        {
            ApplySnapshot(snapshot);
            IsInitialized = true;
        }
    }

    private (DateTime Begin, DateTime End, double? CustomTotal) PrepareRefreshRequest()
    {
        if (!IsCustom)
        {
            GetDateRange(out var begin, out var end, Type);
            DateBegin = begin;
            DateEnd = end;
        }
        double? customTotal = UseCustomTime && CustomTotal > 0.0 ? CustomTotal : null;
        return (DateBegin, DateEnd, customTotal);
    }

    private StatisticsSnapshot? FetchSnapshot(DateTime begin, DateTime end, double? customTotal)
    {
        var beginText = TimeTools.FormatDateTime(begin);
        var endText = TimeTools.FormatDateTime(end);
        var statistics = _statisticsProvider?.Invoke(beginText, endText)
            ?? Db?.GetStatistics(beginText, endText);
        if (statistics is null)
            return null;

        var total = customTotal ?? statistics.Total;
        var detail = new List<StatisticsTimeNode>();
        var times = new List<double>();
        var labels = new List<string>();
        var sum1 = 0.0;
        foreach (var x in statistics.PrimaryTags)
        {
            sum1 += x.Time;
            labels.Add(x.TagName);
            times.Add(x.Time);
            var node = new StatisticsTimeNode()
            {
                Name = x.TagName,
                Time = x.Time,
                Percent = GetPercent(x.Time, total),
                Id = x.TagId,
            };
            if (x.Nested.Count > 0)
            {
                double sum2 = 0.0;
                var nested = new List<StatisticsTimeNode>();
                foreach (var sub in x.Nested)
                {
                    sum2 += sub.Time;
                    nested.Add(new StatisticsTimeNode()
                    {
                        Name = sub.TagName,
                        Percent = GetPercent(sub.Time, total),
                        Time = sub.Time,
                        Id = sub.TagId,
                        Parent = node,
                    });
                }

                if (sum2 < x.Time)
                {
                    nested.Add(new StatisticsTimeNode()
                    {
                        Id = 0,
                        Time = x.Time - sum2,
                        Percent = GetPercent(x.Time - sum2, total),
                        Parent = node,
                    });
                }
                node.Children = nested;
            }

            detail.Add(node);
        }

        if (sum1 < statistics.Total)
        {
            detail.Add(new StatisticsTimeNode()
            {
                Id = 0,
                Time = statistics.Total - sum1,
                Percent = GetPercent(statistics.Total - sum1, total),
            });
        }

        return new StatisticsSnapshot(statistics.Total, times, labels, detail);
    }

    private void ApplySnapshot(StatisticsSnapshot snapshot)
    {
        StatisticsTotal = snapshot.Total;
        Bar.Values = snapshot.Times;
        XAxis.Labels = snapshot.Labels;
        PieChart!.Series = snapshot.Labels
            .Select((label, index) => new PieSeries<double>
            {
                Name = label,
                Values = [snapshot.Times[index]],
            })
            .ToArray();
        TimeDetails!.Items = snapshot.Details;
        TimeDetails.ExpandAll();
    }

    private static double GetPercent(double value, double total)
        => total > 0.0 ? 100.0 * value / total : 0.0;

    private static void GetDateRange(out DateTime begin, out DateTime end, StatisticsType type)
    {
        begin = DateTime.Now.Date;
        end = DateTime.Now.Date;
        switch (type)
        {
            case StatisticsType.LastWeek:
                TimeTools.AdjustDate(ref begin, ref end, AdjustPart.Week, AdjustDirection.Previous);
                break;
            case StatisticsType.LastMonth:
                TimeTools.AdjustDate(ref begin, ref end, AdjustPart.Month, AdjustDirection.Previous);
                break;
            case StatisticsType.LastQuarter:
                TimeTools.AdjustDate(ref begin, ref end, AdjustPart.Quarter, AdjustDirection.Previous);
                break;
            case StatisticsType.LastYear:
                TimeTools.AdjustDate(ref begin, ref end, AdjustPart.Year, AdjustDirection.Previous);
                break;
            case StatisticsType.ThisWeek:
                TimeTools.AdjustDate(ref begin, ref end, AdjustPart.Week, AdjustDirection.Current);
                break;
            case StatisticsType.ThisMonth:
                TimeTools.AdjustDate(ref begin, ref end, AdjustPart.Month, AdjustDirection.Current);
                break;
            case StatisticsType.ThisQuarter:
                TimeTools.AdjustDate(ref begin, ref end, AdjustPart.Quarter, AdjustDirection.Current);
                break;
            case StatisticsType.ThisYear:
                TimeTools.AdjustDate(ref begin, ref end, AdjustPart.Year, AdjustDirection.Current);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    [RelayCommand]
    private void ShowTagDetails(StatisticsTimeNode parameter)
    {
        int[] tagIds;
        if (parameter.Parent is null)
        {
            Debug.Assert(parameter.Id != 0);
            tagIds = [parameter.Id];
        }
        else
        {
            Debug.Assert(parameter.Parent.Id != 0 && parameter.Id != 0);
            tagIds = [parameter.Parent.Id, parameter.Id];
        }
        var items = Db!.QueryWorkItems(new WorkItemQuery
        {
            StartDate = TimeTools.FormatDateTime(DateBegin),
            EndDate = TimeTools.FormatDateTime(DateEnd),
            TagIds = tagIds,
            TagFilter = tagIds.Length == 1 ? WorkItemTagFilter.Any : WorkItemTagFilter.All,
        });

        var sb = new StringBuilder();
        foreach (var item in items)
        {
            sb.AppendLine($"{item.CreateDate}: {item.Comment} ({item.Time:0.##} 小时)");
        }
        EventDispatcher.Notify(
            "详细信息",
            sb.ToString(),
            NotificationRetention.Transient);
    }

    [RelayCommand]
    private void QuickSelectDate(string which)
    {
        Debug.Assert(which.Length == 3);
        var col = which[1] - '0';
        var row = which[2] - '0';

        MakeRange((AdjustPart)row, (AdjustDirection)col);
    }

    public void MakeRange(AdjustPart part, AdjustDirection direction)
    {
        DateTime startDate = DateBegin;
        DateTime endDate = DateEnd;
        TimeTools.AdjustDate(ref startDate, ref endDate, part, direction);
        DateBegin = startDate;
        DateEnd = endDate;

        _ = RefreshSafelyAsync();
    }

    [RelayCommand]
    private void ExpandTree(string open)
    {
        if (open == "1")
            TimeDetails?.ExpandAll();
        else
            TimeDetails?.CollapseAll();
    }

    [RelayCommand]
    private void ToggleExpand(TappedEventArgs args)
    {
        if (UiUtility.TreeDataGridToggleExpand(args.Source as Control))
        {
            args.Handled = true;
        }
    }
}
