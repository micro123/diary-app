using System.Globalization;
using System.Text.RegularExpressions;

namespace Diary.App.Models;

public static partial class TimeExpressionParser
{
    public static bool TryParse(string? expression, out double hours, out string error)
    {
        hours = 0;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "请输入时间，例如 30m 或 1h30m。";
            return false;
        }

        var normalized = expression.Trim().ToLowerInvariant()
            .Replace("小时", "h", StringComparison.Ordinal)
            .Replace("小時", "h", StringComparison.Ordinal)
            .Replace("分钟", "m", StringComparison.Ordinal)
            .Replace("分鐘", "m", StringComparison.Ordinal)
            .Replace("min", "m", StringComparison.Ordinal)
            .Replace("时", "h", StringComparison.Ordinal)
            .Replace("分", "m", StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var plainHours))
            return Validate(plainHours, out hours, out error);

        var match = ExpressionPattern().Match(normalized);
        if (!match.Success)
        {
            error = "无法识别时间格式，请使用 30m、1h30m 或 1小时30分钟。";
            return false;
        }

        var hasHours = match.Groups["hours"].Success;
        var hasMinutes = match.Groups["minutes"].Success;
        var parsedHours = hasHours
            ? double.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture)
            : 0;
        var parsedMinutes = hasMinutes
            ? int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture)
            : 0;
        if (hasHours && hasMinutes && parsedMinutes >= 60)
        {
            error = "小时和分钟同时输入时，分钟必须小于 60。";
            return false;
        }

        return Validate(parsedHours + parsedMinutes / 60.0, out hours, out error);
    }

    [GeneratedRegex("^(?:(?<hours>[0-9]+(?:\\.[0-9]+)?)h)?(?:(?<minutes>[0-9]+)m)?$")]
    private static partial Regex ExpressionPattern();

    private static bool Validate(double value, out double hours, out string error)
    {
        hours = value;
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 24)
        {
            hours = 0;
            error = "时间必须在 0 到 24 小时之间。";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
