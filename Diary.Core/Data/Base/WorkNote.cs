namespace Diary.Core.Data.Base;

public record WorkNote
{
    public int WorkId { get; set; } = 0;
    public string Notes { get; set; } = string.Empty;
}
