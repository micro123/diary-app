using System.Globalization;
using System.Text;
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
        SupportsTemplates: true,
        FormatOptions: []);

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
            var totals = Enumerable.Range(0, table.Columns.Count)
                .Select(_ => new CsvField(string.Empty, true))
                .ToArray();
            var labelColumnIndex = ExportRequestValidator.GetAggregateLabelColumnIndex(table);
            totals[labelColumnIndex] = new CsvField(ExportRequestValidator.GetAggregateLabel(table), true);
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
            return ValidateSimple(lines, context.FileName);
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
        return await RenderSimpleAsync(lines, request, context, cancellationToken);
    }

    private static ExportTemplateValidationResult ValidateSimple(
        IReadOnlyList<string> lines,
        string fileName)
    {
        var markers = new List<ExportTemplateMarker>();
        foreach (var line in lines)
        {
            if (!TryParseRow(line, out var fields))
                return Invalid("EXPORT_TEMPLATE_STRUCTURE_INVALID", "CSV 模板正文包含无效的引号或字段结构。");
            foreach (var field in fields)
                markers.AddRange(ExportTemplateMarkers.Parse(field));
        }

        if (markers.Count == 0)
            return Invalid(
                "EXPORT_TEMPLATE_MARKER_MISSING",
                "CSV 模板至少需要包含一个 {{变量}}、{{items.字段}} 或 {{items.字段|column}} 标记。");

        return new(
            true,
            ExportTemplateMarkers.CreateTemplateName(fileName),
            Path.GetFileNameWithoutExtension(fileName),
            "使用 {{变量}} 和 {{items.字段}} 标记的简易 CSV 模板。",
            "1.0.0",
            ExportTemplateMarkers.InferBindings(markers),
            [ExportFeature.TypedValues, ExportFeature.UnicodeText],
            []);
    }

    private static async ValueTask<ExportRenderResult> RenderSimpleAsync(
        IReadOnlyList<string> lines,
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken)
    {
        var body = new List<string>();
        foreach (var line in lines)
        {
            if (!TryParseRow(line, out var fields))
                throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_STRUCTURE_INVALID",
                    "CSV 模板正文包含无效的引号或字段结构。");
            var markers = fields.SelectMany(ExportTemplateMarkers.Parse).ToArray();
            var matrixMarkers = markers.Where(item => item.Direction == ExportTemplateMarkerDirection.Matrix).ToArray();
            if (matrixMarkers.Length > 0)
            {
                if (matrixMarkers.Length != 1
                    || fields.Count != 1
                    || !string.Equals(fields[0], matrixMarkers[0].Raw, StringComparison.Ordinal))
                    throw new ExportHandlerException(
                        "EXPORT_TEMPLATE_MARKER_INVALID",
                        "CSV 矩阵标记必须独占一整行，例如 {{items|matrix}}。");
                var matrix = GetTable(request, matrixMarkers[0].Collection!);
                ValidateTable(matrix, matrixMarkers[0].Collection!);
                foreach (var (row, rowIndex) in matrix.Rows.Select((row, index) => (row, index)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    body.Add(string.Join(',', row.Select((value, index) => CsvTextSafety.Escape(
                        ConvertMatrixValue(value, matrix.Columns[index], rowIndex + 1)))));
                }
                continue;
            }
            var rowMarkers = markers.Where(item => item.Direction == ExportTemplateMarkerDirection.Row).ToArray();
            var columnMarkers = markers.Where(item => item.Direction == ExportTemplateMarkerDirection.Column).ToArray();
            if (rowMarkers.Length > 0 && columnMarkers.Length > 0)
                throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    "同一 CSV 模板行不能同时进行行循环和列循环。");

            if (rowMarkers.Length > 0)
            {
                var collection = RequireSingleCollection(rowMarkers);
                var table = GetTable(request, collection);
                for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                    body.Add(string.Join(',', fields.Select(field => CsvTextSafety.Escape(
                        ReplaceField(field, request, collection, rowIndex, ExportTemplateMarkerDirection.Row)))));
                continue;
            }

            if (columnMarkers.Length > 0)
            {
                var collection = RequireSingleCollection(columnMarkers);
                var table = GetTable(request, collection);
                var output = new List<string>();
                foreach (var field in fields)
                {
                    var fieldMarkers = ExportTemplateMarkers.Parse(field);
                    if (!fieldMarkers.Any(item => item.Direction == ExportTemplateMarkerDirection.Column))
                    {
                        output.Add(CsvTextSafety.Escape(ReplaceField(field, request, null, null, null)));
                        continue;
                    }

                    for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                        output.Add(CsvTextSafety.Escape(
                            ReplaceField(field, request, collection, rowIndex, ExportTemplateMarkerDirection.Column)));
                }
                body.Add(string.Join(',', output));
                continue;
            }

            body.Add(string.Join(',', fields.Select(field => CsvTextSafety.Escape(
                ReplaceField(field, request, null, null, null)))));
        }

        await File.WriteAllTextAsync(
            context.OutputPath,
            string.Join("\r\n", body) + (body.Count > 0 ? "\r\n" : string.Empty),
            new UTF8Encoding(true),
            cancellationToken);
        return new ExportRenderResult(body.Count);
    }

    private static string ReplaceField(
        string field,
        ExportRequest request,
        string? collection,
        int? rowIndex,
        ExportTemplateMarkerDirection? direction)
    {
        return ExportTemplateMarkers.Replace(field, marker =>
        {
            if (marker.Collection is null)
                return Convert.ToString(
                    request.Template!.Values.GetValueOrDefault(marker.Field),
                    CultureInfo.InvariantCulture);
            if (!string.Equals(marker.Collection, collection, StringComparison.Ordinal))
                throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    $"模板标记使用了多个循环集合：{marker.Collection}。");
            if (rowIndex is null || marker.Direction != direction)
                throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    $"模板标记 {marker.Raw} 的循环方向与所在区域不匹配。");
            var table = GetTable(request, marker.Collection);
            if (!ExportTemplateMarkers.TryGetTableValue(table, marker.Field, rowIndex.Value, out var value, out var type))
                throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_BINDING_INVALID",
                    $"模板标记引用了不存在的表格字段：{marker.Collection}.{marker.Field}。");
            var normalized = ExportRequestValidator.NormalizeValue(value, type, marker.Field, rowIndex.Value + 1);
            return Convert.ToString(normalized, CultureInfo.InvariantCulture);
        });
    }

    private static string RequireSingleCollection(IEnumerable<ExportTemplateMarker> markers)
    {
        var collections = markers.Select(item => item.Collection).Distinct(StringComparer.Ordinal).ToArray();
        if (collections.Length != 1 || collections[0] is null)
            throw new ExportHandlerException(
                "EXPORT_TEMPLATE_MARKER_INVALID",
                "一个循环区域只能使用一个表格绑定。");
        return collections[0]!;
    }

    private static string ConvertMatrixValue(
        object? value,
        ExportColumn column,
        int rowNumber)
    {
        var normalized = ExportRequestValidator.NormalizeValue(value, column.Type, column.Name, rowNumber);
        return Convert.ToString(normalized, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void ValidateTable(ExportTableContent table, string key)
    {
        var validation = ExportRequestValidator.ValidateTableContent(
            table,
            new ExportContentCapabilities(ExportContentKind.Table, [ExportFeature.TypedValues]));
        if (validation is not null)
            throw new ExportHandlerException(
                validation.Code,
                $"CSV 模板表格绑定“{key}”无效：{validation.Message}",
                details: new Dictionary<string, object?>
                {
                    ["binding_key"] = key,
                });
    }

    private static ExportTableContent GetTable(ExportRequest request, string key)
    {
        if (request.Template!.Tables.TryGetValue(key, out var table))
            return table;
        throw new ExportHandlerException(
            "EXPORT_TEMPLATE_REQUIRED_BINDING_MISSING",
            $"缺少模板循环数据：{key}。");
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

    private static ExportTemplateValidationResult Invalid(string code, string message) =>
        new(false, null, null, null, null, [], [], [new ExportDiagnostic(code, message)]);
}
