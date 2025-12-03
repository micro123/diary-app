using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Database;
using Diary.Utils;
using LiveChartsCore;
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
    public string Icon => !IsCustom ? "fa-calendar-check" : "fa-calendar-square";

    private DbInterfaceBase? Db => App.Current.UseDb;

    [ObservableProperty] private DateTime _dateBegin = DateTime.Now.Date;
    [ObservableProperty] private DateTime _dateEnd = DateTime.Now.Date;
    [ObservableProperty] private bool _useCustomTime = false;
    [ObservableProperty] private double _customTotal = 0;
    [ObservableProperty] private ICollection<StatisticsTimeNode>? _timeDetails;

    /// <inheritdoc/>
    public StatisticsTabData(StatisticsType type)
    {
        Name = GetTypeName(type);
        IsCustom = type == StatisticsType.Custom;

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
            // 重新计算时间
        }

        var statistics = Db!.GetStatistics(TimeTools.FormatDateTime(DateBegin), TimeTools.FormatDateTime(DateEnd));

        double total = statistics.Total;
        if (UseCustomTime && CustomTotal > 0.0)
        {
            total = CustomTotal;
        }
        
        var detail = new List<StatisticsTimeNode>();
        var times = new List<double>();
        var labels = new List<string>();
        foreach (var x in statistics.PrimaryTags)
        {
            labels.Add(x.TagName);
            times.Add(x.Time);
            var node = new StatisticsTimeNode()
            {
                Name = x.TagName,
                Time = x.Time,
                Percent = 100.0 * x.Time / total,
            };
            if (x.Nested.Count > 0)
            {
                var nested = new List<StatisticsTimeNode>();
                foreach (var sub in x.Nested)
                {
                    nested.Add(new StatisticsTimeNode()
                    {
                        Name = sub.TagName,
                        Percent = 100.0 * sub.Time / total,
                        Time = sub.Time,
                    });
                }

                node.Children = nested;
            }
            detail.Add(node);
        }

        Bar.Values = times;
        XAxis.Labels = labels;
        TimeDetails = detail;
    }
}