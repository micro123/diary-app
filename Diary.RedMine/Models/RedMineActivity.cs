namespace Diary.RedMine.Models;

public record RedMineActivity
{
    public int Id { get; init; } = 0;
    public string Title { get; init; } = string.Empty;
    public bool Invalid { get; init; }
    public string DisplayTitle => Invalid ? $"{Title} [无效]" : Title;
}
