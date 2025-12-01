using System.Collections.Immutable;

namespace Diary.Core.Data.Statistics;

public class StatisticsResult
{
    public required string DateBegin { get; set; }
    public required string DateEnd { get; set; }
    public double Total { get; set; } = 0;
    public required ICollection<TagTime> PrimaryTags { get; set; }
}