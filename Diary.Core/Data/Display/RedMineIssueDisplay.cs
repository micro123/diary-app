namespace Diary.Core.Data.Display;

public class RedMineIssueDisplay
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string AssignedTo { get; set; }
    public required string Project { get; set; }
    public bool Disabled { get; set; }
}