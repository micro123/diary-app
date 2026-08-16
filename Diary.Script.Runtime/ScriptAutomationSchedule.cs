namespace Diary.Script.Runtime;

public static class ScriptAutomationSchedule
{
    public const string DailyPrefix = "daily ";

    /// <summary>
    /// 解析 V1 调度表达式：仅支持 "daily HH:mm"（24 小时制）。非法返回 false。
    /// </summary>
    public static bool TryParse(string? schedule, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(schedule))
            return false;
        var value = schedule.Trim();
        if (!value.StartsWith(DailyPrefix, StringComparison.Ordinal))
            return false;
        if (!TimeOnly.TryParseExact(value[DailyPrefix.Length..].Trim(), "HH:mm", out var parsed))
            return false;
        time = parsed;
        return true;
    }

    /// <summary>
    /// 计算下一次到期时间。lastRun 为 null 且今天的计划时刻已过时立即到期（启动补跑语义）。
    /// </summary>
    public static DateTimeOffset GetNextDue(TimeOnly time, DateTimeOffset now, DateTimeOffset? lastRun)
    {
        var todayDue = new DateTimeOffset(
            now.Date.Add(time.ToTimeSpan()),
            now.Offset);
        if (lastRun is null)
            return todayDue <= now ? now : todayDue;
        if (todayDue > lastRun.Value && todayDue <= now)
            return now;
        return todayDue <= lastRun.Value ? todayDue.AddDays(1) : todayDue;
    }
}
