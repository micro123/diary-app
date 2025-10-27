namespace Diary.Core.Data.RedMine;

public class WorkTimeEntry
{
    public int WorkId {get; set;}
    public int EntryId {get; set;}
    public int ActivityId {get; set;}
    public int IssueId {get; set;}
    public bool IsUploaded => EntryId != 0;
}