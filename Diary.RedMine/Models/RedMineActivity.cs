namespace Diary.RedMine.Models;

public record RedMineActivity
{
    public int Id { get; init; } = 0;
    public string Title { get; init; } = string.Empty;
}
