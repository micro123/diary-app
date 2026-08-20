using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Diary.ScriptHost;

public enum ExportTemplateMarkerDirection
{
    Scalar,
    Row,
    Column,
    Matrix,
}

public sealed record ExportTemplateMarker(
    string Raw,
    string? Collection,
    string Field,
    ExportTemplateMarkerDirection Direction);

/// <summary>
/// 解析面向普通模板用户的轻量标记。格式插件只负责布局和渲染，脚本负责数据处理。
/// </summary>
public static class ExportTemplateMarkers
{
    private static readonly Regex Marker = new(
        "\\{\\{(?<expression>[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)?(?:\\|column|\\|matrix)?)\\}\\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ExportTemplateMarker> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        return Marker.Matches(text)
            .Select(ParseMatch)
            .ToArray();
    }

    public static string Replace(
        string text,
        Func<ExportTemplateMarker, string?> valueFactory)
    {
        ArgumentNullException.ThrowIfNull(valueFactory);
        return Marker.Replace(text, match => valueFactory(ParseMatch(match)) ?? string.Empty);
    }

    public static IReadOnlyList<ExportBindingDescriptor> InferBindings(
        IEnumerable<ExportTemplateMarker> markers)
    {
        var result = new List<ExportBindingDescriptor>();
        foreach (var marker in markers
                     .Where(item => item.Collection is null)
                     .GroupBy(item => item.Field, StringComparer.Ordinal))
        {
            result.Add(new ExportBindingDescriptor(
                marker.Key,
                ExportBindingKind.Scalar,
                ExportScalarType.Text,
                Description: "模板标记替换值"));
        }

        foreach (var marker in markers
                     .Where(item => item.Collection is not null)
                     .GroupBy(item => item.Collection!, StringComparer.Ordinal))
        {
            result.Add(new ExportBindingDescriptor(
                marker.Key,
                ExportBindingKind.Table,
                Description: "模板循环数据"));
        }

        return result
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool TryGetTableValue(
        ExportTableContent table,
        string field,
        int rowIndex,
        out object? value,
        out ExportColumnType type)
    {
        var columnIndex = -1;
        for (var index = 0; index < table.Columns.Count; index++)
        {
            if (!string.Equals(table.Columns[index].Name, field, StringComparison.OrdinalIgnoreCase))
                continue;
            columnIndex = index;
            break;
        }
        if (columnIndex < 0 || columnIndex >= table.Columns.Count || rowIndex < 0 || rowIndex >= table.Rows.Count)
        {
            value = null;
            type = ExportColumnType.Text;
            return false;
        }

        value = table.Rows[rowIndex][columnIndex];
        type = table.Columns[columnIndex].Type;
        return true;
    }

    public static string CreateTemplateName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var builder = new StringBuilder();
        foreach (var character in stem)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');
        }

        var result = builder.ToString().Trim('_');
        if (string.IsNullOrWhiteSpace(result))
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stem)))
                .ToLowerInvariant()[..8];
            return "template_" + hash;
        }
        if (char.IsDigit(result[0]))
            return "template_" + result;
        return result;
    }

    private static ExportTemplateMarker ParseMatch(Match match)
    {
        var expression = match.Groups["expression"].Value;
        var direction = expression.EndsWith("|column", StringComparison.Ordinal)
            ? ExportTemplateMarkerDirection.Column
            : expression.EndsWith("|matrix", StringComparison.Ordinal)
                ? ExportTemplateMarkerDirection.Matrix
                : ExportTemplateMarkerDirection.Scalar;
        if (direction is ExportTemplateMarkerDirection.Column or ExportTemplateMarkerDirection.Matrix)
            expression = expression[..^(direction == ExportTemplateMarkerDirection.Column ? "|column".Length : "|matrix".Length)];

        var separator = expression.IndexOf('.', StringComparison.Ordinal);
        if (separator < 0)
        {
            if (direction == ExportTemplateMarkerDirection.Matrix)
                return new ExportTemplateMarker(match.Value, expression, string.Empty, direction);
            return new ExportTemplateMarker(
                match.Value,
                null,
                expression,
                ExportTemplateMarkerDirection.Scalar);
        }

        return new ExportTemplateMarker(
            match.Value,
            expression[..separator],
            expression[(separator + 1)..],
            direction == ExportTemplateMarkerDirection.Column
                ? ExportTemplateMarkerDirection.Column
                : ExportTemplateMarkerDirection.Row);
    }
}
