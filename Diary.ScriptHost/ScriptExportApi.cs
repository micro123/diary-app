using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.ScriptBase;
using Diary.Script.Runtime;

namespace Diary.ScriptHost;

public enum DialogDismissPolicy
{
    AllowCancel,
    RequireChoice,
}

public enum OptionDialogStatus
{
    Selected,
    Cancelled,
}

public sealed record DialogOption(
    string Id,
    string Label,
    string? Description = null,
    bool IsDestructive = false);

public sealed record OptionDialogRequest
{
    public string Title { get; init; } = "请选择";
    public string? Message { get; init; }
    public required IReadOnlyList<DialogOption> Options { get; init; }
    public DialogDismissPolicy DismissPolicy { get; init; } = DialogDismissPolicy.AllowCancel;
    public string? DefaultOptionId { get; init; }
}

public sealed record OptionDialogResult(
    OptionDialogStatus Status,
    string? OptionId = null);

public sealed record DirectoryPickerOptions
{
    public string Title { get; init; } = "选择目录";
    public string? SuggestedDirectory { get; init; }
}

public sealed record DirectorySelection(string SelectionId, string DisplayName);

public enum OpenExportedFileStatus
{
    Opened,
    UserDeclined,
    Failed,
}

public sealed record OpenExportedFileResult(
    OpenExportedFileStatus Status,
    ScriptApiError? Error = null);

public enum ExportContentKind
{
    Table,
    Document,
}

public enum ExportColumnType
{
    Text,
    Integer,
    Decimal,
    Date,
    Time,
    Duration,
    DateTime,
    Boolean,
}

public enum ExportAggregation
{
    Sum,
}

public enum ExportTableStyle
{
    Default,
    Compact,
    Report,
}

public enum ExportFeature
{
    UnicodeText,
    TypedValues,
    BackgroundColor,
    MergeCells,
    GeneratedAggregate,
    BasicStyle,
    Paragraphs,
    DocumentTables,
}

public sealed record ExportColumn(
    string Name,
    ExportColumnType Type = ExportColumnType.Text,
    string? NumberFormat = null);

public sealed record ExportAggregateColumn(
    string ColumnName,
    ExportAggregation Aggregation = ExportAggregation.Sum,
    string? Label = null);

public sealed record TableCellMerge(
    int StartRow,
    int StartColumn,
    int RowSpan,
    int ColumnSpan);

public sealed record ExportFormatOptions(
    string FormatId,
    IReadOnlyDictionary<string, object?> Values);

public abstract record ExportContent
{
    public abstract ExportContentKind Kind { get; }
}

public sealed record ExportTableContent : ExportContent
{
    public override ExportContentKind Kind => ExportContentKind.Table;
    public string? Title { get; init; }
    public required IReadOnlyList<ExportColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }
    public IReadOnlyList<ExportAggregateColumn> Aggregates { get; init; } = [];
    public IReadOnlyList<TableCellMerge> Merges { get; init; } = [];
    public ExportTableStyle Style { get; init; } = ExportTableStyle.Default;
}

public sealed record ExportRequest
{
    public required string FormatId { get; init; }
    public required string DirectorySelectionId { get; init; }
    public required string FileName { get; init; }
    public required ExportContent Content { get; init; }
    public ExportFormatOptions? FormatOptions { get; init; }
}

public sealed record ExportContentCapabilities(
    ExportContentKind ContentKind,
    IReadOnlyList<ExportFeature> Features);

public sealed record ExportFormatDescriptor(
    string FormatId,
    string DisplayName,
    string DefaultExtension,
    IReadOnlyList<string> AllowedExtensions,
    IReadOnlyList<ExportContentCapabilities> ContentCapabilities);

public sealed record ExportResult(
    bool Succeeded,
    string FormatId,
    ExportContentKind ContentKind,
    string? FileId,
    string? FileName,
    int? ItemCount,
    ScriptApiError? Error);

public interface IExportApi
{
    ValueTask<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ExportFormatDescriptor>> ListFormatsAsync(
        CancellationToken cancellationToken = default);
}

public interface IFileInteractionApi : IOptionDialogApi
{
    ValueTask<DirectorySelection?> PickDirectoryAsync(
        DirectoryPickerOptions options,
        CancellationToken cancellationToken = default);

    ValueTask<OpenExportedFileResult> AskToOpenExportedFileAsync(
        string fileId,
        CancellationToken cancellationToken = default);
}

public sealed class ExportRequestValidator
{
    public const int MaxRows = 100_000;
    public const int MaxColumns = 256;
    public const int MaxFileNameLength = 240;

    public static ScriptApiError? Validate(ExportRequest request, ExportFormatDescriptor descriptor)
    {
        if (!string.Equals(request.FormatId, descriptor.FormatId, StringComparison.Ordinal))
            return Error("导出格式与格式目录不一致。");
        if (string.IsNullOrWhiteSpace(request.DirectorySelectionId))
            return Error("目录选择令牌不能为空。");
        if (string.IsNullOrWhiteSpace(request.FileName))
            return Error("文件名不能为空。");
        if (request.FileName.Length > MaxFileNameLength
            || request.FileName is "." or ".."
            || request.FileName.Any(char.IsControl)
            || request.FileName.Contains('/')
            || request.FileName.Contains('\\'))
            return Error("文件名包含非法字符或路径分隔符。");

        var extension = Path.GetExtension(request.FileName);
        if (string.IsNullOrEmpty(extension))
            extension = descriptor.DefaultExtension;
        if (!descriptor.AllowedExtensions.Any(x => string.Equals(x, extension, StringComparison.OrdinalIgnoreCase)))
            return Error("文件扩展名与导出格式不匹配。");
        if (request.Content is not ExportTableContent table)
            return Error("当前导出处理器只支持 table 内容。");
        if (table.Columns.Count is 0 or > MaxColumns)
            return Error("导出列数量超出限制。");
        if (table.Rows.Count > MaxRows)
            return Error("导出数据行数超出限制。");
        if (table.Columns.Any(column => string.IsNullOrWhiteSpace(column.Name)))
            return Error("导出列名不能为空。");
        foreach (var row in table.Rows)
        {
            if (row.Count != table.Columns.Count)
                return Error("导出数据行的单元格数量与列数量不一致。");
        }

        var columnNames = table.Columns.Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var aggregate in table.Aggregates)
        {
            if (!columnNames.Contains(aggregate.ColumnName))
                return Error($"合计列不存在：{aggregate.ColumnName}。");
            var column = table.Columns.First(column =>
                string.Equals(column.Name, aggregate.ColumnName, StringComparison.OrdinalIgnoreCase));
            if (column.Type is not (ExportColumnType.Integer or ExportColumnType.Decimal or ExportColumnType.Duration))
                return Error($"列“{aggregate.ColumnName}”不支持 Sum 合计。");
        }

        foreach (var merge in table.Merges)
        {
            if (merge.StartRow < 1 || merge.StartColumn < 1 || merge.RowSpan < 1 || merge.ColumnSpan < 1
                || merge.StartRow + merge.RowSpan - 1 > table.Rows.Count
                || merge.StartColumn + merge.ColumnSpan - 1 > table.Columns.Count)
                return Error("合并区域超出表格边界。");
        }

        for (var i = 0; i < table.Merges.Count; i++)
        {
            var left = table.Merges[i];
            var leftEndRow = left.StartRow + left.RowSpan - 1;
            var leftEndColumn = left.StartColumn + left.ColumnSpan - 1;
            for (var j = i + 1; j < table.Merges.Count; j++)
            {
                var right = table.Merges[j];
                var rightEndRow = right.StartRow + right.RowSpan - 1;
                var rightEndColumn = right.StartColumn + right.ColumnSpan - 1;
                if (left.StartRow <= rightEndRow && right.StartRow <= leftEndRow
                    && left.StartColumn <= rightEndColumn && right.StartColumn <= leftEndColumn)
                    return Error("合并区域不能相互重叠。");
            }
        }

        return null;
    }

    public static object? NormalizeValue(object? value, ExportColumnType type, string columnName, int rowNumber)
    {
        if (value is null)
            return null;
        try
        {
            return type switch
            {
                ExportColumnType.Text => value.ToString(),
                ExportColumnType.Integer => value switch
                {
                    int i => i,
                    long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
                    _ => int.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
                },
                ExportColumnType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                ExportColumnType.Boolean => value is bool b ? b : bool.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
                ExportColumnType.Date => DateOnly.ParseExact(Convert.ToString(value, CultureInfo.InvariantCulture)!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ExportColumnType.Time => TimeOnly.ParseExact(Convert.ToString(value, CultureInfo.InvariantCulture)!, "HH:mm:ss", CultureInfo.InvariantCulture),
                ExportColumnType.Duration => ParseDuration(value),
                ExportColumnType.DateTime => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("不支持的列类型。"),
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new FormatException($"第 {rowNumber} 行的“{columnName}”值无法转换为 {type}。", exception);
        }
    }

    private static TimeSpan ParseDuration(object value)
    {
        if (value is TimeSpan duration)
            return duration;
        if (value is double seconds)
            return TimeSpan.FromSeconds(seconds);
        if (value is float floatSeconds)
            return TimeSpan.FromSeconds(floatSeconds);
        if (value is decimal decimalSeconds)
            return TimeSpan.FromSeconds((double)decimalSeconds);
        if (value is int intSeconds)
            return TimeSpan.FromSeconds(intSeconds);
        if (value is long longSeconds)
            return TimeSpan.FromSeconds(longSeconds);

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var parts = text.Split(':', StringSplitOptions.TrimEntries);
        var secondsPart = 0d;
        if (parts.Length is < 2 or > 3
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || (parts.Length == 3
                && !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out secondsPart)))
            throw new FormatException("持续时间必须是秒数或 HH:mm:ss。");
        var secondsValue = parts.Length == 3 ? secondsPart : 0;
        if (hours < 0 || minutes is < 0 or > 59 || secondsValue < 0 || secondsValue >= 60)
            throw new FormatException("持续时间范围无效。");
        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(secondsValue);
    }

    private static ScriptApiError Error(string message) =>
        new("EXPORT_INVALID_REQUEST", message, ScriptErrorCategory.Validation);
}

public interface IOptionDialogApi
{
    ValueTask<OptionDialogResult> SelectOptionAsync(
        OptionDialogRequest request,
        CancellationToken cancellationToken = default);
}

public interface IContextualExportApi : IExportApi
{
    ValueTask<ExportResult> ExportAsync(
        ExportRequest request,
        ScriptHostCallContext context,
        CancellationToken cancellationToken = default);
}

public static class ExportJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
        options.Converters.Add(new ExportContentJsonConverter());
        return options;
    }

    private sealed class ExportContentJsonConverter : JsonConverter<ExportContent>
    {
        public override ExportContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var kind = root.GetProperty("kind").GetString();
            if (!string.Equals(kind, "table", StringComparison.Ordinal))
                throw new JsonException("只支持 table 导出内容。");
            var columns = root.GetProperty("columns").Deserialize<List<ExportColumn>>(options) ?? [];
            var rows = root.GetProperty("rows").EnumerateArray()
                .Select(row => row.EnumerateArray().Select(ToObject).ToArray())
                .Cast<IReadOnlyList<object?>>()
                .ToArray();
            return new ExportTableContent
            {
                Title = root.TryGetProperty("title", out var title) ? title.GetString() : null,
                Columns = columns,
                Rows = rows,
                Aggregates = root.TryGetProperty("aggregates", out var aggregates)
                    ? aggregates.Deserialize<List<ExportAggregateColumn>>(options) ?? []
                    : [],
                Merges = root.TryGetProperty("merges", out var merges)
                    ? merges.Deserialize<List<TableCellMerge>>(options) ?? []
                    : [],
                Style = root.TryGetProperty("style", out var style)
                    ? style.Deserialize<ExportTableStyle>(options)
                    : ExportTableStyle.Default,
            };
        }

        public override void Write(Utf8JsonWriter writer, ExportContent value, JsonSerializerOptions options)
        {
            if (value is not ExportTableContent table)
                throw new JsonException("只支持 table 导出内容。");
            writer.WriteStartObject();
            writer.WriteString("kind", "table");
            if (table.Title is not null)
                writer.WriteString("title", table.Title);
            writer.WritePropertyName("columns");
            JsonSerializer.Serialize(writer, table.Columns, options);
            writer.WritePropertyName("rows");
            JsonSerializer.Serialize(writer, table.Rows, options);
            writer.WritePropertyName("aggregates");
            JsonSerializer.Serialize(writer, table.Aggregates, options);
            writer.WritePropertyName("merges");
            JsonSerializer.Serialize(writer, table.Merges, options);
            writer.WriteString("style", table.Style.ToString().ToLowerInvariant());
            writer.WriteEndObject();
        }

        private static object? ToObject(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.GetRawText(),
        };
    }
}

public static class CsvTextSafety
{
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@'];

    public static string ProtectFormulaText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > 0 && FormulaPrefixes.Contains(value[0]) ? "'" + value : value;
    }

    public static string Escape(string? value)
    {
        var text = ProtectFormulaText(value ?? string.Empty);
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? '"' + text.Replace("\"", "\"\"") + '"'
            : text;
    }
}
