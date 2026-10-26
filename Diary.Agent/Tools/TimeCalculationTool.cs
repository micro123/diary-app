using System.Globalization;
using System.Text.Json;

namespace Diary.Agent.Tools;

public sealed class TimeCalculationTool : IAgentTool
{
    private const int MaximumYears = 100;
    private const int MaximumMonths = 1200;
    private const int MaximumDays = 36600;
    private const int MaximumHours = 100000;
    private const int MaximumMinutes = 1000000;

    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.CalculateTime;

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        string? error = null;
        if (arguments.ValueKind != JsonValueKind.Object
            || !TryGetRequiredString(arguments, "operation", out var operation, out error))
        {
            return Failure(error ?? "参数必须是 JSON 对象。");
        }

        return operation switch
        {
            "add" => ValueTask.FromResult(Add(arguments)),
            "difference" => ValueTask.FromResult(Difference(arguments)),
            _ => Failure("operation 必须是 add 或 difference。"),
        };
    }

    private static AgentToolResult Add(JsonElement arguments)
    {
        if (!TryGetRequiredString(arguments, "value", out var value, out var error))
            return AgentToolResult.Failure("invalid_arguments", error!);
        if (!TryGetInteger(arguments, "years", MaximumYears, out var years, out error)
            || !TryGetInteger(arguments, "months", MaximumMonths, out var months, out error)
            || !TryGetInteger(arguments, "days", MaximumDays, out var days, out error)
            || !TryGetInteger(arguments, "hours", MaximumHours, out var hours, out error)
            || !TryGetInteger(arguments, "minutes", MaximumMinutes, out var minutes, out error))
        {
            return AgentToolResult.Failure("invalid_arguments", error!);
        }

        if (TryParseDate(value, out var date))
        {
            if (hours != 0 || minutes != 0)
            {
                return AgentToolResult.Failure(
                    "invalid_arguments",
                    "纯日期 value 只能使用 years、months 和 days；小时或分钟计算请传入带 UTC 偏移的 ISO 8601 日期时间。");
            }
            try
            {
                var result = date.AddYears(years).AddMonths(months).AddDays(days);
                return AgentToolResult.Success(JsonSerializer.Serialize(new
                {
                    operation = "add",
                    inputKind = "date",
                    input = FormatDate(date),
                    result = FormatDate(result),
                    dayOfWeek = result.DayOfWeek.ToString(),
                    applied = new { years, months, days, hours, minutes },
                }), "Calculation");
            }
            catch (ArgumentOutOfRangeException)
            {
                return AgentToolResult.Failure("out_of_range", "日期计算结果超出支持范围。");
            }
        }

        if (!TryParseDateTime(value, out var dateTime))
            return AgentToolResult.Failure("invalid_arguments", DateTimeFormatError("value"));
        try
        {
            var result = dateTime
                .AddYears(years)
                .AddMonths(months)
                .AddDays(days)
                .AddHours(hours)
                .AddMinutes(minutes);
            return AgentToolResult.Success(JsonSerializer.Serialize(new
            {
                operation = "add",
                inputKind = "dateTime",
                input = FormatDateTime(dateTime),
                result = FormatDateTime(result),
                utcResult = result.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
                dayOfWeek = result.DayOfWeek.ToString(),
                applied = new { years, months, days, hours, minutes },
            }), "Calculation");
        }
        catch (ArgumentOutOfRangeException)
        {
            return AgentToolResult.Failure("out_of_range", "日期时间计算结果超出支持范围。");
        }
    }

    private static AgentToolResult Difference(JsonElement arguments)
    {
        if (!TryGetRequiredString(arguments, "start", out var start, out var error)
            || !TryGetRequiredString(arguments, "end", out var end, out error))
        {
            return AgentToolResult.Failure("invalid_arguments", error!);
        }

        var startIsDate = TryParseDate(start, out var startDate);
        var endIsDate = TryParseDate(end, out var endDate);
        if (startIsDate || endIsDate)
        {
            if (!startIsDate || !endIsDate)
                return AgentToolResult.Failure("invalid_arguments", "start 和 end 必须同为纯日期或同为带 UTC 偏移的日期时间。");
            var calendarDays = endDate.DayNumber - startDate.DayNumber;
            return AgentToolResult.Success(JsonSerializer.Serialize(new
            {
                operation = "difference",
                inputKind = "date",
                start = FormatDate(startDate),
                end = FormatDate(endDate),
                calendarDays,
                absoluteCalendarDays = Math.Abs(calendarDays),
                relation = DescribeRelation(calendarDays),
            }), "Calculation");
        }

        if (!TryParseDateTime(start, out var startDateTime))
            return AgentToolResult.Failure("invalid_arguments", DateTimeFormatError("start"));
        if (!TryParseDateTime(end, out var endDateTime))
            return AgentToolResult.Failure("invalid_arguments", DateTimeFormatError("end"));
        var difference = endDateTime - startDateTime;
        return AgentToolResult.Success(JsonSerializer.Serialize(new
        {
            operation = "difference",
            inputKind = "dateTime",
            start = FormatDateTime(startDateTime),
            end = FormatDateTime(endDateTime),
            totalSeconds = difference.TotalSeconds,
            totalMinutes = difference.TotalMinutes,
            totalHours = difference.TotalHours,
            totalDays = difference.TotalDays,
            relation = DescribeRelation(difference.Ticks),
        }), "Calculation");
    }

    private static bool TryGetRequiredString(
        JsonElement arguments,
        string propertyName,
        out string value,
        out string? error)
    {
        value = string.Empty;
        error = null;
        if (!arguments.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            error = $"{propertyName} 必须是非空字符串。";
            return false;
        }
        value = element.GetString()!.Trim();
        return true;
    }

    private static bool TryGetInteger(
        JsonElement arguments,
        string propertyName,
        int maximumAbsoluteValue,
        out int value,
        out string? error)
    {
        value = 0;
        error = null;
        if (!arguments.TryGetProperty(propertyName, out var element))
            return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value))
        {
            error = $"{propertyName} 必须是整数。";
            return false;
        }
        if (Math.Abs((long)value) > maximumAbsoluteValue)
        {
            error = $"{propertyName} 必须在 {-maximumAbsoluteValue} 到 {maximumAbsoluteValue} 之间。";
            return false;
        }
        return true;
    }

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private static bool TryParseDateTime(string value, out DateTimeOffset dateTime)
    {
        dateTime = default;
        if (!HasExplicitOffset(value))
            return false;
        return DateTimeOffset.TryParseExact(
            value,
            [
                "yyyy-MM-dd'T'HH:mm:ssK",
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
            ],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out dateTime);
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z'))
            return true;
        return value.Length >= 6
               && (value[^6] == '+' || value[^6] == '-')
               && value[^3] == ':';
    }

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatDateTime(DateTimeOffset dateTime) =>
        dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

    private static string DescribeRelation(long difference) => difference switch
    {
        > 0 => "end_after_start",
        < 0 => "end_before_start",
        _ => "equal",
    };

    private static string DateTimeFormatError(string propertyName) =>
        $"{propertyName} 必须是 yyyy-MM-dd，或带 UTC 偏移的 ISO 8601 日期时间，例如 2026-10-26T08:30:00+08:00。";

    private static ValueTask<AgentToolResult> Failure(string message) =>
        ValueTask.FromResult(AgentToolResult.Failure("invalid_arguments", message));
}
