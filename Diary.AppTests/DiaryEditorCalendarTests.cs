using Diary.App.ViewModels;

namespace Diary.AppTests;

[TestClass]
public sealed class DiaryEditorCalendarTests
{
    [TestMethod]
    [DataRow(2026, 8, 25, "2026年8月 第35周")]
    [DataRow(2027, 1, 1, "2027年1月 第1周")]
    public void CompactCalendarTitleIncludesCalendarWeek(int year, int month, int day, string expected)
    {
        Assert.AreEqual(expected, DiaryEditorViewModel.FormatCompactCalendarTitle(new DateTime(year, month, day)));
    }

    [TestMethod]
    public void TrackerUploadWeekRangeUsesMondayThroughSunday()
    {
        var range = DiaryEditorViewModel.GetTrackerUploadRange(
            new DateTime(2026, 8, 25),
            Diary.Utils.AdjustPart.Week);

        Assert.AreEqual(new DateTime(2026, 8, 24), range.StartDate);
        Assert.AreEqual(new DateTime(2026, 8, 30), range.EndDate);
        Assert.AreEqual("本周", range.PeriodName);
    }

    [TestMethod]
    public void TrackerUploadMonthRangeUsesWholeCalendarMonth()
    {
        var range = DiaryEditorViewModel.GetTrackerUploadRange(
            new DateTime(2026, 8, 25),
            Diary.Utils.AdjustPart.Month);

        Assert.AreEqual(new DateTime(2026, 8, 1), range.StartDate);
        Assert.AreEqual(new DateTime(2026, 8, 31), range.EndDate);
        Assert.AreEqual("本月", range.PeriodName);
    }
}
