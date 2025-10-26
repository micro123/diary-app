namespace Diary.Core.Data.Base;

public class WorkTag
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public int Color { get; set; }
    public TagLevels Level { get; set; }
    public bool Disabled { get; set; }
}
