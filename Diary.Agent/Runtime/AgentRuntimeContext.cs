using System.Globalization;

namespace Diary.Agent.Runtime;

public sealed record AgentRuntimeContextSnapshot(
    DateTimeOffset LocalDateTime,
    string LocalDate,
    string DayOfWeek,
    string TimeZoneId,
    DateTimeOffset UtcDateTime,
    string UtcOffset,
    string Yesterday,
    string Tomorrow)
{
    public static AgentRuntimeContextSnapshot Capture(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var utcNow = timeProvider.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeProvider.LocalTimeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        return new AgentRuntimeContextSnapshot(
            localNow,
            localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            localDate.DayOfWeek.ToString(),
            timeProvider.LocalTimeZone.Id,
            utcNow,
            FormatOffset(localNow.Offset),
            localDate.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            localDate.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public string ToSystemBlock() => $"""
        current_local_datetime: {LocalDateTime.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)}
        current_local_date: {LocalDate}
        day_of_week: {DayOfWeek}
        time_zone: {TimeZoneId}
        utc_offset: {UtcOffset}
        current_utc_datetime: {UtcDateTime.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}
        yesterday: {Yesterday}
        tomorrow: {Tomorrow}
        """;

    private static string FormatOffset(TimeSpan offset) =>
        $"{(offset < TimeSpan.Zero ? "-" : "+")}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
}
