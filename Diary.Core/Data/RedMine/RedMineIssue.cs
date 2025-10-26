namespace Diary.Core.Data.RedMine;

public class RedMineIssue
{
    public int Id { get; init; }
    public required string Title { get; init; }
    public required string AssignedTo { get; init; }
    public int ProjectId { get; init; } 
    public bool IsClosed { get; init; }
}