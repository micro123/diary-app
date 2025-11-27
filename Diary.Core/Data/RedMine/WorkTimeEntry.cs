namespace Diary.Core.Data.RedMine;

public record WorkTimeEntry
{
    public int WorkId {get; set;}
    public int EntryId {get; set;}
    public int ActivityId {get; set;}
    public int IssueId {get; set;}
}

