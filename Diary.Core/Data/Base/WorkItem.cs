namespace Diary.Core.Data.Base;

public class WorkItem
{
    public int Id { get; set; }
    public required string Date { get; set; }
    public required string Comment { get; set; }
    public required string Notes { get; set; }
    public double Time { get; set; }
    public WorkPriorities Priority { get; set; }
}
