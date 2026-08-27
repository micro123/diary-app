namespace Diary.App.Models;

public enum PeriodWorkTimeSubmissionCategory
{
    Submitted,
    Unsubmitted,
    BlockedOrFailed,
}

public sealed record PeriodWorkTimeSummaryEntry(
    double Hours,
    WorkItemUploadStatus UploadStatus,
    bool IsImportedReadOnly,
    PeriodTrackerUploadEligibility Eligibility);

public sealed record PeriodWorkTimeSummaryBucket(int ItemCount, double Hours);

public sealed record PeriodWorkTimeSummary(
    DateTime StartDate,
    DateTime EndDate,
    PeriodWorkTimeSummaryBucket Total,
    PeriodWorkTimeSummaryBucket Submitted,
    PeriodWorkTimeSummaryBucket Unsubmitted,
    PeriodWorkTimeSummaryBucket BlockedOrFailed);

public static class PeriodWorkTimeSummaryCalculator
{
    public static PeriodWorkTimeSummary Calculate(
        DateTime startDate,
        DateTime endDate,
        IEnumerable<PeriodWorkTimeSummaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var snapshots = entries.ToArray();
        var submitted = CreateBucket(snapshots, PeriodWorkTimeSubmissionCategory.Submitted);
        var unsubmitted = CreateBucket(snapshots, PeriodWorkTimeSubmissionCategory.Unsubmitted);
        var blockedOrFailed = CreateBucket(snapshots, PeriodWorkTimeSubmissionCategory.BlockedOrFailed);
        return new PeriodWorkTimeSummary(
            startDate.Date,
            endDate.Date,
            new PeriodWorkTimeSummaryBucket(snapshots.Length, snapshots.Sum(entry => entry.Hours)),
            submitted,
            unsubmitted,
            blockedOrFailed);
    }

    public static PeriodWorkTimeSubmissionCategory Classify(PeriodWorkTimeSummaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsImportedReadOnly
            || entry.UploadStatus is WorkItemUploadStatus.PartialFailure
                or WorkItemUploadStatus.Failed
                or WorkItemUploadStatus.Uncertain)
        {
            return PeriodWorkTimeSubmissionCategory.BlockedOrFailed;
        }

        if (entry.UploadStatus == WorkItemUploadStatus.Synchronized)
            return PeriodWorkTimeSubmissionCategory.Submitted;

        return entry.Eligibility.CanUpload
            ? PeriodWorkTimeSubmissionCategory.Unsubmitted
            : PeriodWorkTimeSubmissionCategory.BlockedOrFailed;
    }

    private static PeriodWorkTimeSummaryBucket CreateBucket(
        IReadOnlyCollection<PeriodWorkTimeSummaryEntry> entries,
        PeriodWorkTimeSubmissionCategory category)
    {
        var matching = entries.Where(entry => Classify(entry) == category).ToArray();
        return new PeriodWorkTimeSummaryBucket(
            matching.Length,
            matching.Sum(entry => entry.Hours));
    }
}
