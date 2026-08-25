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
}
