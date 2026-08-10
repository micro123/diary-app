using Diary.PluginBase;

namespace Diary.RedMine.Models;

public record WorkTimeEntry
{
    public int WorkId { get; set; }
    public int EntryId { get; set; }
    public int ActivityId { get; set; }
    public int IssueId { get; set; }
    public TrackerUploadState UploadState { get; set; } = TrackerUploadState.NotAttempted;
    public string? UploadError { get; set; }
    public DateTimeOffset? UploadAttemptedAt { get; set; }
}
