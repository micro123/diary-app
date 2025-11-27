namespace Diary.Core.Data.RedMine;

public record RedMineIssue
{
    public int Id { get; init; } = 0;
    public string Title { get; init; } = string.Empty;
    public string AssignedTo { get; init; } = string.Empty;
    public int ProjectId { get; init; } = 0;
    public bool IsClosed { get; init; } = false;
}