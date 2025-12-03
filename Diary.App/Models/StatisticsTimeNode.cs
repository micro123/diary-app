using System.Collections.Generic;

namespace Diary.App.Models;

public record StatisticsTimeNode
{
    public required string Name { get; set; }
    public double Time { get; set; }
    public double Percent { get; set; }
    public ICollection<StatisticsTimeNode>? Children { get; set; }
}