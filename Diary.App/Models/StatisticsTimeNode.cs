using System;
using System.Collections.Generic;

namespace Diary.App.Models;

public sealed class StatisticsTimeNode
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public double Time { get; set; }
    public double Percent { get; set; }
    public ICollection<StatisticsTimeNode> Children { get; set; } = Array.Empty<StatisticsTimeNode>();
    public object? GetValue(object? _, string key)
    {
        return key switch
        {
            nameof(Id) => Id,
            nameof(Name) => Name,
            nameof(Time) => Time,
            nameof(Percent) => Percent,
            nameof(Children) => Children,
            _ => null
        };
    }
}