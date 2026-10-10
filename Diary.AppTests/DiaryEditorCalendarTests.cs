using Avalonia.Controls.Notifications;
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
    public void MonthCalendarTitleOmitsWeekNumber()
    {
        Assert.AreEqual(
            "2026年8月",
            DiaryEditorViewModel.FormatCompactCalendarTitle(
                new DateTime(2026, 8, 25),
                WorkRecordCalendarView.Month));
    }

    [TestMethod]
    [DataRow("周视图", WorkRecordCalendarView.Week)]
    [DataRow("月视图", WorkRecordCalendarView.Month)]
    [DataRow("Week", WorkRecordCalendarView.Week)]
    [DataRow("Month", WorkRecordCalendarView.Month)]
    [DataRow("unknown", WorkRecordCalendarView.Week)]
    public void CalendarViewPreferenceSupportsLocalizedAndLegacyValues(
        string value,
        WorkRecordCalendarView expected)
    {
        Assert.AreEqual(expected, DiaryEditorViewModel.ParseCompactCalendarView(value));
    }

    [TestMethod]
    public void EntryCountBadgeReplacesFilledIndicatorWhenVisible()
    {
        var day = new CompactCalendarDay
        {
            Date = new DateTime(2026, 10, 10),
            DayText = "10",
            HasEntries = true,
        };

        Assert.IsTrue(day.IsFilledIndicatorVisible);
        Assert.IsFalse(day.IsEntryCountVisible);

        day.IsEntryCountVisible = true;

        Assert.IsFalse(day.IsFilledIndicatorVisible);
        Assert.IsTrue(day.IsEntryCountVisible);
    }

    [TestMethod]
    public void WeekCalendarRangeStartsOnMondayAndContainsSevenDays()
    {
        var range = DiaryEditorViewModel.GetCompactCalendarRange(
            new DateTime(2026, 8, 25),
            WorkRecordCalendarView.Week);

        Assert.AreEqual(new DateTime(2026, 8, 24), range.FirstDate);
        Assert.AreEqual(7, range.DayCount);
    }

    [TestMethod]
    [DataRow(2026, 8, 1, 42)]
    [DataRow(2026, 9, 1, 35)]
    [DataRow(2027, 2, 1, 28)]
    public void MonthCalendarRangeUsesOnlyRequiredWholeWeeks(int year, int month, int day, int expectedDays)
    {
        var range = DiaryEditorViewModel.GetCompactCalendarRange(
            new DateTime(year, month, day),
            WorkRecordCalendarView.Month);

        Assert.AreEqual(DayOfWeek.Monday, range.FirstDate.DayOfWeek);
        Assert.AreEqual(expectedDays, range.DayCount);
        Assert.AreEqual(0, range.DayCount % 7);
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
    public void SuccessfulDailyUploadUsesSuccessNotificationWithoutFailureText()
    {
        var notification = DiaryEditorViewModel.CreateBatchUploadNotification(3, 0, 0);

        Assert.AreEqual("同步完成：成功 3", notification.Title);
        Assert.AreEqual(NotificationType.Success, notification.Type);
        Assert.DoesNotContain("失败", notification.Title);
    }

    [TestMethod]
    public void DailyUploadNotificationReflectsFailureAndUncertainResults()
    {
        var failed = DiaryEditorViewModel.CreateBatchUploadNotification(2, 1, 0);
        var uncertain = DiaryEditorViewModel.CreateBatchUploadNotification(2, 0, 1);

        Assert.AreEqual(NotificationType.Error, failed.Type);
        Assert.Contains("失败 1", failed.Title);
        Assert.AreEqual(NotificationType.Warning, uncertain.Type);
        Assert.Contains("结果待确认 1", uncertain.Title);
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
