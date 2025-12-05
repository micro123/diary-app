using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Database;
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
    private readonly StatisticsType _type;

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

    private static string GetTypeName(StatisticsType statisticType) => Names[(int)statisticType];
    public string Name { get; init; }
    public bool IsCustom { get; init; }
    public string Icon => !IsCustom ? "fa-calendar-check" : "fa-calendar";

    private DbInterfaceBase? Db => App.Current.UseDb;

    [ObservableProperty] private DateTime _dateBegin = TimeTools.FromFormatedDate("2025-11-01");
    [ObservableProperty] private DateTime _dateEnd = TimeTools.FromFormatedDate("2025-11-30");
    [ObservableProperty] private bool _useCustomTime = false;
    [ObservableProperty] private double _customTotal = 0;
    [ObservableProperty] private double _statisticsTotal = 0;

    private HierarchicalTreeDataGridSource<StatisticsTimeNode> _timeDetails;
    public ITreeDataGridSource TimeDetails => _timeDetails;

    /// <inheritdoc/>
    public StatisticsTabData(StatisticsType type)
    {
        _type = type;
        Name = GetTypeName(type);
        IsCustom = type == StatisticsType.Custom;

        _timeDetails = new HierarchicalTreeDataGridSource<StatisticsTimeNode>([])
        {
            Columns =
            {
                new HierarchicalExpanderColumn<StatisticsTimeNode>(
                    new TextColumn<StatisticsTimeNode,int>(
                        null,
                        x=>x.Id,
                        (o,v) => o.Id = v!,
                        new GridLength(80, GridUnitType.Pixel),
                        new() { StringFormat = "#{0}", CanUserResizeColumn = false, CanUserSortColumn = false, BeginEditGestures = BeginEditGestures.None }
                    ),
                    x=>x.Children,
                    x => x.Children.Count > 0),
                new TextColumn<StatisticsTimeNode,string>(
                    "标签",
                    x=>x.Name,
                    (o,v) => o.Name = v!,
                    new GridLength(1, GridUnitType.Star),
                    new() { StringFormat = "#{0}", CanUserResizeColumn = false, CanUserSortColumn = false, BeginEditGestures = BeginEditGestures.None }
                    ),
                new TextColumn<StatisticsTimeNode,double>(
                    "耗时",
                    x=>x.Time,
                    (o,v) => o.Time = v!,
                    new GridLength(120, GridUnitType.Pixel),
                    new() { StringFormat = "{0:0.##} 小时", CanUserResizeColumn = false, CanUserSortColumn = false, BeginEditGestures = BeginEditGestures.None }
                ),
                new TextColumn<StatisticsTimeNode,double>(
                    "占比",
                    x=>x.Percent,
                    (o,v) => o.Percent = v!,
                    new GridLength(120, GridUnitType.Pixel),
                    new() { StringFormat = "{0:0.##} %", CanUserResizeColumn = false, CanUserSortColumn = false, BeginEditGestures = BeginEditGestures.None }
                ),
                new TemplateColumn<StatisticsTimeNode>(
                    "操作",
                    "OperationsCell",
                    options: new() { CanUserResizeColumn = false, CanUserSortColumn = false, BeginEditGestures = BeginEditGestures.None }
                ),
            }
        };

        InitChart();
        FetchData();
    }

    private void InitChart()
    {
        Chart.Series = [Bar];
        Chart.XAxes =
        [
            XAxis
        ];
        Chart.LegendPosition = LegendPosition.Hidden;
        Chart.ZoomMode = ZoomAndPanMode.None;
        Chart.EasingFunction = null; // disable animations
    }

    public CartesianChart Chart { get; } = new();
    private ColumnSeries<double> Bar = new() { Name = "工时" };
    private Axis XAxis = new() { Name = "项目" };

    [RelayCommand]
    private async Task Refresh()
    {
        await Task.Run(FetchData);
    }

    private void FetchData()
    {
        if (!IsCustom)
        {
            GetDateRange(out var s, out var e, _type);
            DateBegin = s;
            DateEnd = e;
        }

        var statistics = Db!.GetStatistics(TimeTools.FormatDateTime(DateBegin), TimeTools.FormatDateTime(DateEnd));

        double total = statistics.Total;
        StatisticsTotal = total;
        if (UseCustomTime && CustomTotal > 0.0)
        {
            total = CustomTotal;
        }

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
                Percent = 100.0 * x.Time / total,
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
                        Percent = 100.0 * sub.Time / total,
                        Time = sub.Time,
                        Id = x.TagId,
                    });
                }

                if (sum2 < x.Time)
                {
                    nested.Add(new StatisticsTimeNode()
                    {
                        Id = 0,
                        Name = "未分类",
                        Percent = 100.0 * (x.Time - sum2) / total,
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
                Name = "未分类",
                Percent = 100.0 * (statistics.Total - sum1) / total,
            });
        }

        Bar.Values = times;
        XAxis.Labels = labels;
        _timeDetails.Items = detail;
    }



    private static void GetDateRange(out DateTime begin, out DateTime end, StatisticsType type)
    {
        DateTime today = DateTime.Today.Date;
        int subtract;
        switch (type)
        {
            case StatisticsType.LastWeek:
                subtract = (int)today.DayOfWeek + 7;
                begin = today.AddDays(-subtract);
                end = begin.AddDays(6);
                break;
            case StatisticsType.LastMonth:
                begin = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                end = begin.AddMonths(1).AddDays(-1);
                break;
            case StatisticsType.LastQuarter:
            {
                int q = (today.Month - 1) / 3;
                int y = today.Year;
                if (q == 0)
                {
                    q = 3;
                    --y;
                }

                begin = new DateTime(y, q * 3 + 1, 1);
                end = begin.AddMonths(3).AddDays(-1);
            }
                break;
            case StatisticsType.LastYear:
                begin = new DateTime(today.Year-1, 1, 1);
                end = new DateTime(today.Year, 12, 31);
                break;
            case StatisticsType.ThisWeek:
                subtract = (int)today.DayOfWeek;
                begin = today.AddDays(-subtract);
                end = begin.AddDays(6);
                break;
            case StatisticsType.ThisMonth:
                begin = new DateTime(today.Year, today.Month, 1);
                end = begin.AddMonths(1).AddDays(-1);
                break;
            case StatisticsType.ThisQuarter:
            {
                int q = (today.Month - 1) / 3;
                begin = new DateTime(today.Year, q * 3 + 1, 1);
                end = begin.AddMonths(3).AddDays(-1);
            }
                break;
            case StatisticsType.ThisYear:
                begin = new DateTime(today.Year, 1, 1);
                end = new DateTime(today.Year, 12, 31);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }
}