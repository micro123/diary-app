namespace Diary.Core.Data.RedMine;

public class RedMineProject
{
    public int Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool IsClosed { get; init; }
}