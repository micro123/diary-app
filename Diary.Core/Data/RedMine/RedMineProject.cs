namespace Diary.Core.Data.RedMine;

public record RedMineProject
{
    public int Id { get; init; } = 0;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsClosed { get; init; } = false;
}