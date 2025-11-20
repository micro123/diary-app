namespace Diary.Core.Data.Base;

public class WorkTag
{
    public int Id { get; set; } = 0;
    public required string Name { get; set; }
    public int Color { get; set; } = 0;
    public TagLevels Level { get; set; } = TagLevels.Primary;
    public bool Disabled { get; set; } = false;
}
