using Avalonia.Input;
using Diary.App.ViewModels;
using Diary.App.Views;

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

    [TestMethod]
    public void EmptyEditorScriptMenuContainsDisabledPlaceholder()
    {
        var menu = DiaryEditorViewModel.CreateEmptyEditorScriptMenu("脚本（本月）");

        Assert.IsTrue(menu.Enabled);
        Assert.AreEqual("脚本（本月）", menu.Header);
        Assert.HasCount(1, menu.Children);
        Assert.AreEqual("暂无", menu.Children[0].Header);
        Assert.IsFalse(menu.Children[0].Enabled);
        Assert.IsNull(menu.Children[0].Command);
    }

    [TestMethod]
    public void AltArrowShortcutMapsToCalendarDirectionWhenDiaryIsVisible()
    {
        Assert.AreEqual(-1, MainWindow.ResolveDiaryDateNavigationOffset(Key.Left, KeyModifiers.Alt, true));
        Assert.AreEqual(1, MainWindow.ResolveDiaryDateNavigationOffset(Key.Right, KeyModifiers.Alt, true));
        Assert.AreEqual(-7, MainWindow.ResolveDiaryDateNavigationOffset(Key.Up, KeyModifiers.Alt, true));
        Assert.AreEqual(7, MainWindow.ResolveDiaryDateNavigationOffset(Key.Down, KeyModifiers.Alt, true));
    }

    [TestMethod]
    public void AltArrowShortcutRequiresDiaryPageAndExactModifier()
    {
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(Key.Left, KeyModifiers.Alt, false));
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(Key.Left, KeyModifiers.None, true));
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(
            Key.Left,
            KeyModifiers.Alt | KeyModifiers.Shift,
            true));
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(Key.PageDown, KeyModifiers.Alt, true));
    }
}
