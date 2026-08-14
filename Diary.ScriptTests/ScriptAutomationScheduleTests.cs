using Diary.Script.Runtime;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptAutomationScheduleTests
{
    [TestMethod]
    public void TryParse_AcceptsValidDailySchedule()
    {
        Assert.IsTrue(ScriptAutomationSchedule.TryParse("daily 09:30", out var time));
        Assert.AreEqual(9, time.Hour);
        Assert.AreEqual(30, time.Minute);
        Assert.IsTrue(ScriptAutomationSchedule.TryParse("daily 23:59", out _));
        Assert.IsTrue(ScriptAutomationSchedule.TryParse("daily 00:00", out _));
    }

    [TestMethod]
    public void TryParse_RejectsInvalidSchedule()
    {
        Assert.IsFalse(ScriptAutomationSchedule.TryParse(null, out _));
        Assert.IsFalse(ScriptAutomationSchedule.TryParse("", out _));
        Assert.IsFalse(ScriptAutomationSchedule.TryParse("9:30", out _));
        Assert.IsFalse(ScriptAutomationSchedule.TryParse("daily 9:30", out _));
        Assert.IsFalse(ScriptAutomationSchedule.TryParse("daily 24:00", out _));
        Assert.IsFalse(ScriptAutomationSchedule.TryParse("daily 09:60", out _));
        Assert.IsFalse(ScriptAutomationSchedule.TryParse("hourly", out _));
    }

    [TestMethod]
    public void GetNextDue_UsesStartupCatchUpSemanticsWhenNeverRun()
    {
        var offset = TimeSpan.FromHours(8);
        var time = new TimeOnly(9, 0);
        var beforeDue = new DateTimeOffset(2026, 8, 14, 8, 0, 0, offset);
        var afterDue = new DateTimeOffset(2026, 8, 14, 10, 0, 0, offset);

        var upcoming = ScriptAutomationSchedule.GetNextDue(time, beforeDue, null);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 14, 9, 0, 0, offset), upcoming);

        // 从未运行且当天时刻已过 → 立即到期（启动补跑语义）
        var catchUp = ScriptAutomationSchedule.GetNextDue(time, afterDue, null);
        Assert.AreEqual(afterDue, catchUp);
    }

    [TestMethod]
    public void GetNextDue_SkipsAlreadyRunOccurrence()
    {
        var offset = TimeSpan.FromHours(8);
        var time = new TimeOnly(9, 0);
        var now = new DateTimeOffset(2026, 8, 14, 10, 30, 0, offset);
        var ranToday = new DateTimeOffset(2026, 8, 14, 9, 0, 0, offset);

        // 今天的 occurrence 已运行过 → 明天
        var next = ScriptAutomationSchedule.GetNextDue(time, now, ranToday);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 15, 9, 0, 0, offset), next);

        // 上次运行是昨天 → 今天 occurrence 已过 → 立即到期
        var ranYesterday = new DateTimeOffset(2026, 8, 13, 9, 0, 0, offset);
        var catchUp = ScriptAutomationSchedule.GetNextDue(time, now, ranYesterday);
        Assert.AreEqual(now, catchUp);
    }
}
