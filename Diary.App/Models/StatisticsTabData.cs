using System;
using CommunityToolkit.Mvvm.ComponentModel;

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

public partial class StatisticsTabData: ObservableObject
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
    
    public string Name { get; set; }
    public string Icon { get; set; }
    public bool   CanClose { get; set; }

    public StatisticsTabData(StatisticsType type)
    {
        Name = GetTypeName(type);
        Icon = type == StatisticsType.Custom ? "fa-pen-to-square" : "fa-calendar-check";
        CanClose = type != StatisticsType.Custom;
    }
}