using Diary.App.Models;

namespace Diary.AppTests;

[TestClass]
public sealed class PeriodWorkTimeSummaryTests
{
    [TestMethod]
    public void Calculate_GroupsItemsAndHoursIntoExclusiveSubmissionBuckets()
    {
        var eligible = PeriodTrackerUploadEligibility.Eligible;
        var blocked = PeriodTrackerUploadEligibility.Skip(
            PeriodTrackerUploadSkipKind.TrackerIncomplete,
            "信息不完整");

        var summary = PeriodWorkTimeSummaryCalculator.Calculate(
            new DateTime(2026, 8, 24),
            new DateTime(2026, 8, 30),
            [
                Entry(2, WorkItemUploadStatus.Synchronized, blocked),
                Entry(3, WorkItemUploadStatus.Pending, eligible),
                Entry(1.5, WorkItemUploadStatus.Pending, blocked),
                Entry(4, WorkItemUploadStatus.Failed, eligible),
                Entry(2, WorkItemUploadStatus.PartialFailure, eligible),
                Entry(1, WorkItemUploadStatus.Uncertain, eligible),
                Entry(5, WorkItemUploadStatus.Synchronized, blocked, imported: true),
            ]);

        Assert.AreEqual(7, summary.Total.ItemCount);
        Assert.AreEqual(18.5, summary.Total.Hours, 0.001);
        Assert.AreEqual(new PeriodWorkTimeSummaryBucket(1, 2), summary.Submitted);
        Assert.AreEqual(new PeriodWorkTimeSummaryBucket(1, 3), summary.Unsubmitted);
        Assert.AreEqual(new PeriodWorkTimeSummaryBucket(5, 13.5), summary.BlockedOrFailed);
        Assert.AreEqual(
            summary.Total.ItemCount,
            summary.Submitted.ItemCount + summary.Unsubmitted.ItemCount + summary.BlockedOrFailed.ItemCount);
        Assert.AreEqual(
            summary.Total.Hours,
            summary.Submitted.Hours + summary.Unsubmitted.Hours + summary.BlockedOrFailed.Hours,
            0.001);
    }

    [TestMethod]
    public void Calculate_EmptyRangeReturnsZeroBuckets()
    {
        var summary = PeriodWorkTimeSummaryCalculator.Calculate(
            new DateTime(2026, 8, 1),
            new DateTime(2026, 8, 31),
            []);

        Assert.AreEqual(new PeriodWorkTimeSummaryBucket(0, 0), summary.Total);
        Assert.AreEqual(new PeriodWorkTimeSummaryBucket(0, 0), summary.Submitted);
        Assert.AreEqual(new PeriodWorkTimeSummaryBucket(0, 0), summary.Unsubmitted);
        Assert.AreEqual(new PeriodWorkTimeSummaryBucket(0, 0), summary.BlockedOrFailed);
    }

    private static PeriodWorkTimeSummaryEntry Entry(
        double hours,
        WorkItemUploadStatus status,
        PeriodTrackerUploadEligibility eligibility,
        bool imported = false) => new(hours, status, imported, eligibility);
}
