namespace Diary.RedMine.Models;

public record RedMineIssueDisplay
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string AssignedTo { get; set; }
    public required string Project { get; set; }
    public bool Disabled { get; set; }
    public bool Invalid { get; set; }
    public string DisplayTitle => Disabled || Invalid ? $"{Title} [无效]" : Title;
}
