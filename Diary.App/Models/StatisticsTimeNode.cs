using System;
using System.Collections.Generic;

namespace Diary.App.Models;

public sealed class StatisticsTimeNode
{
    public int Id { get; set; }
    public string Name { get; set; } = "**未分类**";
    public double Time { get; set; }
    public double Percent { get; set; }
    public ICollection<StatisticsTimeNode> Children { get; set; } = Array.Empty<StatisticsTimeNode>();
    public StatisticsTimeNode? Parent { get; set; }

    public bool CanShowDetails => Id != 0;
    public bool IsUncategorized => Id == 0;
}