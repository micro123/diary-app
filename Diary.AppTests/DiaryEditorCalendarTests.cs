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
    public void AltJklSemicolonShortcutMapsToShiftedVimDirections()
    {
        Assert.AreEqual(-1, ResolveShortcut(Key.J, PhysicalKey.J));
        Assert.AreEqual(7, ResolveShortcut(Key.K, PhysicalKey.K));
        Assert.AreEqual(-7, ResolveShortcut(Key.L, PhysicalKey.L));
        Assert.AreEqual(1, ResolveShortcut(Key.OemSemicolon, PhysicalKey.Semicolon));
        Assert.AreEqual(1, ResolveShortcut(Key.None, PhysicalKey.Semicolon));
    }

    [TestMethod]
    public void AltJklSemicolonShortcutRequiresDiaryPageAndExactModifier()
    {
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(
            Key.J,
            PhysicalKey.J,
            KeyModifiers.Alt,
            false));
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(
            Key.J,
            PhysicalKey.J,
            KeyModifiers.None,
            true));
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(
            Key.J,
            PhysicalKey.J,
            KeyModifiers.Alt | KeyModifiers.Shift,
            true));
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(
            Key.PageDown,
            PhysicalKey.PageDown,
            KeyModifiers.Alt,
            true));
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(
            Key.Left,
            PhysicalKey.ArrowLeft,
            KeyModifiers.Alt,
            true));
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(
            Key.Right,
            PhysicalKey.ArrowRight,
            KeyModifiers.Alt,
            true));
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(
            Key.Up,
            PhysicalKey.ArrowUp,
            KeyModifiers.Alt,
            true));
        Assert.IsNull(MainWindow.ResolveDiaryDateNavigationOffset(
            Key.Down,
            PhysicalKey.ArrowDown,
            KeyModifiers.Alt,
            true));
    }

    private static int? ResolveShortcut(Key key, PhysicalKey physicalKey = PhysicalKey.None) =>
        MainWindow.ResolveDiaryDateNavigationOffset(key, physicalKey, KeyModifiers.Alt, true);
}
