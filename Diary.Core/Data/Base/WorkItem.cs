namespace Diary.Core.Data.Base;

public class WorkItem
{
    public int Id { get; set; } = 0;
    public string CreateDate { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public double Time { get; set; } = 0.0;
    public WorkPriorities Priority { get; set; } = WorkPriorities.P0;
}
