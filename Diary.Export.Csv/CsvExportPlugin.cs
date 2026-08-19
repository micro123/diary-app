using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Diary.ScriptHost;

namespace Diary.Export.Csv;

public sealed class CsvExportPlugin : IExportPlugin
{
    private readonly CsvTableExportHandler _handler = new();
    private readonly CsvTemplateHandler _templateHandler = new();

    public ExportPluginManifest Manifest { get; } = new("csv", "1.0.0");

    public IEnumerable<IExportHandler> GetExportHandlers() => [_handler];

    public IEnumerable<IExportTemplateHandler> GetTemplateHandlers() => [_templateHandler];
}

internal sealed class CsvTableExportHandler : IExportHandler
{
    public ExportFormatDescriptor Descriptor { get; } = new(
        "csv",
        "CSV 文本",
        ".csv",
        [".csv"],
        [new ExportContentCapabilities(
            ExportContentKind.Table,
            [ExportFeature.UnicodeText, ExportFeature.TypedValues, ExportFeature.GeneratedAggregate])],
        SupportsTemplates: true);

    public async ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.Content is not ExportTableContent table)
            throw new InvalidOperationException("CSV 通用导出只支持 table 内容。");
        if (request.FormatOptions is not null)
            throw new ExportHandlerException("EXPORT_INVALID_REQUEST", "CSV 不支持格式选项。", retryable: false);
        if (table.Merges.Count > 0)
            throw new ExportHandlerException("EXPORT_UNSUPPORTED_FEATURE", "CSV 不支持合并单元格。", retryable: false);
        if (table.Style != ExportTableStyle.Default)
            throw new ExportHandlerException("EXPORT_UNSUPPORTED_FEATURE", "CSV 不支持视觉样式。", retryable: false);

        await using var stream = new FileStream(
            context.OutputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            bufferSize: 64 * 1024,
            leaveOpen: false)
        {
            NewLine = "\r\n",
        };

        if (!string.IsNullOrWhiteSpace(table.Title))
            await WriteRowAsync(writer, [new CsvField(table.Title, true)], cancellationToken);
        await WriteRowAsync(writer, table.Columns.Select(column => new CsvField(column.Name, true)), cancellationToken);

        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = table.Rows[rowIndex];
            var values = row.Select((value, index) =>
            {
                var column = table.Columns[index];
                return new CsvField(
                    FormatValue(
                        ExportRequestValidator.NormalizeValue(value, column.Type, column.Name, rowIndex + 1),
                        column),
                    column.Type == ExportColumnType.Text);
            }).ToArray();
            await WriteRowAsync(writer, values, cancellationToken);
        }

        if (table.Aggregates.Count > 0)
        {
            var totals = new CsvField[table.Columns.Count];
            totals[0] = new CsvField("合计", true);
            foreach (var aggregate in table.Aggregates)
            {
                var columnIndex = table.Columns
                    .Select((column, index) => (column, index))
                    .First(item => string.Equals(item.column.Name, aggregate.ColumnName, StringComparison.OrdinalIgnoreCase)).index;
                totals[columnIndex] = new CsvField(FormatAggregate(table, columnIndex), false);
            }
            await WriteRowAsync(writer, totals, cancellationToken);
        }

        return new ExportRenderResult(table.Rows.Count);
    }

    private static async Task WriteRowAsync(
        StreamWriter writer,
        IEnumerable<CsvField> values,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(string.Join(',', values.Select(value => CsvTextSafety.Escape(value.Value, value.ProtectFormula)))).WaitAsync(cancellationToken);
    }

    private sealed record CsvField(string Value, bool ProtectFormula);

    private static string FormatValue(object? value, ExportColumn column) => column.Type switch
    {
        ExportColumnType.Date when value is DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ExportColumnType.Time when value is TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        ExportColumnType.Duration when value is TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
        ExportColumnType.DateTime when value is DateTimeOffset dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        ExportColumnType.Boolean when value is bool boolean => boolean ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static string FormatAggregate(ExportTableContent table, int columnIndex)
    {
        var type = table.Columns[columnIndex].Type;
        if (type == ExportColumnType.Duration)
        {
            var duration = table.Rows
                .Select(row => ExportRequestValidator.NormalizeValue(
                    row[columnIndex], type, table.Columns[columnIndex].Name, 0))
                .OfType<TimeSpan>()
                .Aggregate(TimeSpan.Zero, (total, item) => total + item);
            return duration.ToString("c", CultureInfo.InvariantCulture);
        }

        var total = table.Rows
            .Select(row => ExportRequestValidator.NormalizeValue(
                row[columnIndex], type, table.Columns[columnIndex].Name, 0))
            .Select(value => Convert.ToDecimal(value, CultureInfo.InvariantCulture))
            .Sum();
        return total.ToString(CultureInfo.InvariantCulture);
    }
}



internal sealed class CsvTemplateHandler : IExportTemplateHandler
{
    private static readonly Regex BindingLine = new(
        "^#\\s*binding\\s*:\\s*(?<key>[a-z][a-z0-9]*(?:_[a-z0-9]+)*)\\s*\\|\\s*scalar\\s*\\|\\s*(?<type>[a-z]+)\\s*\\|\\s*(?<required>true|false)\\s*(?:\\|\\s*(?<default>.*))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Placeholder = new("\\{\\{(?<key>[a-z][a-z0-9]*(?:_[a-z0-9]+)*)\\}\\}", RegexOptions.Compiled);

    public string PluginId => "csv";
    public string FormatId => "csv";
    public IReadOnlyList<string> SupportedTemplateExtensions => [".csv"];

    public async ValueTask<ExportTemplateValidationResult> ValidateAsync(
        Stream templateStream,
        ExportTemplateValidationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(context.FileExtension, ".csv", StringComparison.OrdinalIgnoreCase))
            return Invalid("EXPORT_TEMPLATE_EXTENSION_INVALID", "CSV 模板扩展名必须为 .csv。");
        try
        {
            using var reader = new StreamReader(templateStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var lines = new List<string>();
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lines.Add(line);
            }
            if (lines.Count < 3 || !string.Equals(lines[0].Trim(), "# diary.export.template", StringComparison.Ordinal))
                return Invalid("EXPORT_TEMPLATE_METADATA_MISSING", "CSV 模板缺少元数据头。");
            var name = MetadataValue(lines[1], "template_name");
            var version = MetadataValue(lines[2], "version");
            var bindings = new List<ExportBindingDescriptor>();
            var bindingKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 3; index < lines.Count; index++)
            {
                if (!lines[index].StartsWith('#'))
                    continue;
                var match = BindingLine.Match(lines[index]);
                if (!match.Success)
                {
                    if (lines[index].StartsWith("# binding", StringComparison.OrdinalIgnoreCase))
                        return Invalid("EXPORT_TEMPLATE_BINDING_INVALID", "CSV 模板绑定声明格式无效。");
                    continue;
                }
                var key = match.Groups["key"].Value;
                if (!bindingKeys.Add(key))
                    return Invalid("EXPORT_TEMPLATE_BINDING_DUPLICATE", $"CSV 模板绑定键重复：{key}。");
                if (!Enum.TryParse<ExportScalarType>(match.Groups["type"].Value, true, out var scalarType))
                    return Invalid("EXPORT_TEMPLATE_BINDING_TYPE_INVALID", $"CSV 模板绑定类型无效：{key}。");
                var hasDefault = match.Groups["default"].Success && !string.IsNullOrWhiteSpace(match.Groups["default"].Value);
                bindings.Add(new ExportBindingDescriptor(
                    key,
                    ExportBindingKind.Scalar,
                    scalarType,
                    bool.Parse(match.Groups["required"].Value),
                    hasDefault,
                    hasDefault ? ParseDefault(match.Groups["default"].Value, scalarType) : null));
            }
            var placeholders = lines.Skip(3).SelectMany(line => Placeholder.Matches(line).Select(match => match.Groups["key"].Value));
            foreach (var key in placeholders.Distinct(StringComparer.Ordinal))
            {
                if (bindings.All(binding => !string.Equals(binding.Key, key, StringComparison.Ordinal)))
                    bindings.Add(new ExportBindingDescriptor(key, ExportBindingKind.Scalar, ExportScalarType.Text));
            }
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
                return Invalid("EXPORT_TEMPLATE_METADATA_INVALID", "CSV 模板名和版本不能为空。");
            var firstDataLine = 0;
            while (firstDataLine < lines.Count && lines[firstDataLine].StartsWith('#'))
                firstDataLine++;
            if (lines.Skip(firstDataLine).Any(line => !TryParseRow(line, out _)))
                return Invalid("EXPORT_TEMPLATE_STRUCTURE_INVALID", "CSV 模板正文包含无效的引号或字段结构。");
            return new(true, name, name, null, version, bindings, [ExportFeature.TypedValues, ExportFeature.UnicodeText], []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Invalid("EXPORT_TEMPLATE_STRUCTURE_INVALID", $"无法读取 CSV 模板：{exception.Message}");
        }
    }

    public async ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.Template is null)
            throw new InvalidOperationException("CSV 模板导出请求缺少 template。");
        await using var input = await context.OpenTemplateAsync(cancellationToken);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
            lines.Add(line);
        var firstDataLine = 0;
        while (firstDataLine < lines.Count && lines[firstDataLine].StartsWith('#'))
            firstDataLine++;
        var body = new List<string>();
        foreach (var line in lines.Skip(firstDataLine))
        {
            if (!TryParseRow(line, out var fields))
                throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_STRUCTURE_INVALID",
                    "CSV 模板正文包含无效的引号或字段结构。");
            body.Add(string.Join(',', fields.Select(field => CsvTextSafety.Escape(
                Placeholder.Replace(field, match =>
                    Convert.ToString(
                        request.Template.Values.GetValueOrDefault(match.Groups["key"].Value),
                        CultureInfo.InvariantCulture) ?? string.Empty)))));
        }
        await File.WriteAllTextAsync(
            context.OutputPath,
            string.Join("\r\n", body) + (body.Count > 0 ? "\r\n" : string.Empty),
            new UTF8Encoding(true),
            cancellationToken);
        return new ExportRenderResult(body.Count);
    }

    private static bool TryParseRow(string line, out IReadOnlyList<string> fields)
    {
        var result = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        var quoteClosed = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quoted)
            {
                if (character != '"')
                {
                    value.Append(character);
                    continue;
                }
                if (index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                    continue;
                }
                quoted = false;
                quoteClosed = true;
                continue;
            }

            if (character == ',')
            {
                result.Add(value.ToString());
                value.Clear();
                quoteClosed = false;
                continue;
            }
            if (character == '"')
            {
                if (value.Length > 0 || quoteClosed)
                {
                    fields = [];
                    return false;
                }
                quoted = true;
                continue;
            }
            if (quoteClosed)
            {
                fields = [];
                return false;
            }
            value.Append(character);
        }

        if (quoted)
        {
            fields = [];
            return false;
        }
        result.Add(value.ToString());
        fields = result;
        return true;
    }

    private static string? MetadataValue(string line, string key) =>
        line.StartsWith("# " + key + ":", StringComparison.OrdinalIgnoreCase)
            ? line[(key.Length + 3)..].Trim()
            : null;

    private static object ParseDefault(string value, ExportScalarType type) => type switch
    {
        ExportScalarType.Integer when int.TryParse(value, out var integer) => integer,
        ExportScalarType.Decimal when decimal.TryParse(value, out var decimalValue) => decimalValue,
        ExportScalarType.Boolean when bool.TryParse(value, out var boolean) => boolean,
        ExportScalarType.Duration when TimeSpan.TryParse(value, out var duration) => duration,
        _ => value,
    };

    private static ExportTemplateValidationResult Invalid(string code, string message) =>
        new(false, null, null, null, null, [], [], [new ExportDiagnostic(code, message)]);
}
