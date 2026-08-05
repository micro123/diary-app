using System.Globalization;
using System.Text.RegularExpressions;

namespace Diary.PluginBase;

/// <summary>
/// 插件依赖使用的轻量语义版本范围匹配器。
/// 支持精确/部分版本、通配符、比较运算符、^ 和 ~，以及空格或逗号连接的比较条件。
/// </summary>
public static partial class PluginVersionRange
{
    private readonly record struct VersionValue(int Major, int Minor, int Patch)
        : IComparable<VersionValue>
    {
        public int CompareTo(VersionValue other)
            => Major != other.Major ? Major.CompareTo(other.Major)
                : Minor != other.Minor ? Minor.CompareTo(other.Minor)
                : Patch.CompareTo(other.Patch);

        public override string ToString() => $"{Major}.{Minor}.{Patch}";
    }

    [GeneratedRegex(@"^(?<op>\^|~|>=|<=|>|<|=)?\s*v?(?<version>[^\s,]+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ConditionPattern();

    [GeneratedRegex(@"^(?<major>\d+)(?:\.(?<minor>\d+|x|X|\*))?(?:\.(?<patch>\d+|x|X|\*))?$")]
    private static partial Regex PartialVersionPattern();

    public static bool IsSatisfied(string version, string? range, out string? error)
    {
        if (!TryParse(version, out var actual, out error))
            return false;

        if (string.IsNullOrWhiteSpace(range) || range.Trim() is "*")
        {
            error = null;
            return true;
        }

        foreach (var expression in range.Replace(',', ' ').Split(
                     ' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseCondition(expression, out var condition, out error)
                || !Matches(actual, condition))
            {
                error ??= $"插件版本 {version} 不满足条件 {expression}";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool Matches(VersionValue actual, Condition condition)
    {
        var comparison = actual.CompareTo(condition.Version);
        if (condition.UpperBound is { } upperBound)
        {
            return comparison >= 0 && actual.CompareTo(upperBound) < 0;
        }

        return condition.Operator switch
        {
            ">" => comparison > 0,
            ">=" => comparison >= 0,
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            _ => comparison == 0,
        };
    }

    private static bool TryParseCondition(
        string expression,
        out Condition condition,
        out string? error)
    {
        var match = ConditionPattern().Match(expression);
        if (!match.Success)
        {
            condition = default;
            error = $"版本范围条件无效：{expression}";
            return false;
        }

        var op = match.Groups["op"].Value;
        op = string.IsNullOrEmpty(op) ? null : op;
        var versionText = match.Groups["version"].Value;
        var partial = PartialVersionPattern().Match(versionText);
        if (!partial.Success || !int.TryParse(
                partial.Groups["major"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var major))
        {
            condition = default;
            error = $"版本范围版本号无效：{versionText}";
            return false;
        }

        var minorText = partial.Groups["minor"].Value;
        var patchText = partial.Groups["patch"].Value;
        var hasMinor = !string.IsNullOrEmpty(minorText);
        var hasPatch = !string.IsNullOrEmpty(patchText);
        var minorWildcard = !hasMinor || IsWildcard(minorText);
        var patchWildcard = !hasPatch || IsWildcard(patchText);

        if ((op is "^" or "~" or ">" or ">=" or "<" or "<=" or "=")
            && (minorWildcard || patchWildcard))
        {
            condition = default;
            error = $"比较运算符必须使用完整版本号：{versionText}";
            return false;
        }

        if (!int.TryParse(
                minorWildcard ? "0" : minorText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var minor)
            || !int.TryParse(
                patchWildcard ? "0" : patchText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var patch))
        {
            condition = default;
            error = $"版本范围版本号无效：{versionText}";
            return false;
        }

        var parsed = new VersionValue(major, minor, patch);
        if (op is null or "=" && (minorWildcard || patchWildcard))
        {
            // 1 和 1.x 表示 [1.0.0, 2.0.0)，1.2 和 1.2.x 表示 [1.2.0, 1.3.0)。
            var upper = minorWildcard
                ? new VersionValue(major + 1, 0, 0)
                : new VersionValue(major, minor + 1, 0);
            condition = new Condition("range", parsed, upper);
        }
        else if (op == "^")
        {
            var upper = major > 0
                ? new VersionValue(major + 1, 0, 0)
                : minor > 0
                    ? new VersionValue(0, minor + 1, 0)
                    : new VersionValue(0, 0, patch + 1);
            condition = new Condition(op, parsed, upper);
        }
        else if (op == "~")
        {
            condition = new Condition(op, parsed, new VersionValue(major, minor + 1, 0));
        }
        else
        {
            condition = new Condition(op, parsed, null);
        }

        error = null;
        return true;
    }

    private static bool TryParse(string text, out VersionValue version, out string? error)
    {
        var match = PartialVersionPattern().Match(text.Trim().TrimStart('v'));
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, out var major)
            || !int.TryParse(match.Groups["minor"].Value is { Length: > 0 } minorText
                && !IsWildcard(minorText) ? minorText : "0", out var minor)
            || !int.TryParse(match.Groups["patch"].Value is { Length: > 0 } patchText
                && !IsWildcard(patchText) ? patchText : "0", out var patch))
        {
            version = default;
            error = $"插件版本无效：{text}";
            return false;
        }

        version = new VersionValue(major, minor, patch);
        error = null;
        return true;
    }

    private static bool IsWildcard(string text)
        => text is "x" or "X" or "*";

    private readonly record struct Condition(
        string? Operator,
        VersionValue Version,
        VersionValue? UpperBound);
}
