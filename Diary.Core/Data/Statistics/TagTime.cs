namespace Diary.Core.Data.Statistics;

public class TagTime
{
    public int TagId { get; set; }
    public double Time { get; set; } = 0;
    public required string TagName { get; set; }
    public ICollection<TagTime> Nested { get; set; } = Array.Empty<TagTime>();
}