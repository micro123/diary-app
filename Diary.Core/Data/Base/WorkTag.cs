namespace Diary.Core.Data.Base;

public record WorkTag
{
    public int Id { get; set; } = 0;
    public string Name { get; set; } = string.Empty;
    public int Color { get; set; } = 0;
    public TagLevels Level { get; set; } = TagLevels.Primary;
    public bool Disabled { get; set; } = false;
}
