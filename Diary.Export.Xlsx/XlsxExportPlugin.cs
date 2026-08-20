using ClosedXML.Excel;
using Diary.ScriptHost;
using System.Text.RegularExpressions;

namespace Diary.Export.Xlsx;

public sealed class XlsxExportPlugin : IExportPlugin
{
    private readonly XlsxTableExportHandler _exportHandler = new();
    private readonly XlsxTemplateHandler _handler = new();

    public ExportPluginManifest Manifest { get; } = new("xlsx", "1.0.0");

    public IEnumerable<IExportHandler> GetExportHandlers() => [_exportHandler];

    public IEnumerable<IExportTemplateHandler> GetTemplateHandlers() => [_handler];
}

internal sealed class XlsxTemplateHandler : IExportTemplateHandler
{
    private static readonly Regex DangerousFormula = new(
        "(?:^|[^A-Z])(?:WEBSERVICE|FILTERXML|HYPERLINK|RTD|DDE)\\s*\\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExternalWorkbookReference = new(
        "\\[[^\\]]+\\][^!]+!",
        RegexOptions.Compiled);

    public string PluginId => "xlsx";
    public string FormatId => "xlsx";
    public IReadOnlyList<string> SupportedTemplateExtensions => [".xlsx"];

    public ValueTask<ExportTemplateValidationResult> ValidateAsync(
        Stream templateStream,
        ExportTemplateValidationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(context.FileExtension, ".xlsx", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(Invalid("EXPORT_TEMPLATE_EXTENSION_INVALID", "XLSX 模板扩展名必须为 .xlsx。"));

        try
        {
            var diagnostics = OpenXmlTemplateSafety.ValidatePackage(templateStream).ToList();
            if (diagnostics.Count > 0)
                return ValueTask.FromResult(new ExportTemplateValidationResult(
                    false, null, null, null, null, [], [], diagnostics));

            templateStream.Position = 0;
            using var workbook = new XLWorkbook(templateStream);
            var markerCells = FindMarkerCells(workbook).ToArray();
            var markers = markerCells.SelectMany(item => item.Markers).ToArray();
            if (markers.Length == 0)
                return ValueTask.FromResult(Invalid(
                    "EXPORT_TEMPLATE_MARKER_MISSING",
                    "XLSX 模板至少需要包含一个 {{变量}} 或 {{items.字段}} 标记。"));

            ValidateLoopDirections(markerCells, diagnostics);
            ValidateFormulaSafety(workbook, diagnostics);
            if (diagnostics.Count > 0)
                return ValueTask.FromResult(new ExportTemplateValidationResult(
                    false,
                    ExportTemplateMarkers.CreateTemplateName(context.FileName),
                    Path.GetFileNameWithoutExtension(context.FileName),
                    "使用简易标记的 XLSX 模板。",
                    "1.0.0",
                    ExportTemplateMarkers.InferBindings(markers),
                    [],
                    diagnostics));

            return ValueTask.FromResult(new ExportTemplateValidationResult(
                true,
                ExportTemplateMarkers.CreateTemplateName(context.FileName),
                Path.GetFileNameWithoutExtension(context.FileName),
                "使用简易标记的 XLSX 模板。",
                "1.0.0",
                ExportTemplateMarkers.InferBindings(markers),
                [ExportFeature.TypedValues, ExportFeature.BasicStyle],
                []));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Invalid(
                "EXPORT_TEMPLATE_STRUCTURE_INVALID",
                $"无法读取 XLSX 模板：{exception.Message}"));
        }
    }

    public async ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Template is null)
            throw new InvalidOperationException("模板导出请求缺少 template。");

        await using var templateStream = await context.OpenTemplateAsync(cancellationToken);
        using var workbook = new XLWorkbook(templateStream);
        var itemCount = 0;
        foreach (var worksheet in workbook.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markerCells = FindMarkerCells(worksheet).ToArray();
            var matrixCells = markerCells
                .Where(item => item.Markers.Any(marker => marker.Direction == ExportTemplateMarkerDirection.Matrix))
                .OrderByDescending(item => item.Cell.Address.RowNumber)
                .ThenByDescending(item => item.Cell.Address.ColumnNumber)
                .ToArray();
            var rowNumbers = markerCells
                .Where(item => item.Markers.Any(marker => marker.Direction == ExportTemplateMarkerDirection.Row))
                .Select(item => item.Cell.Address.RowNumber)
                .Distinct()
                .OrderDescending()
                .ToArray();
            var columnNumbers = markerCells
                .Where(item => item.Markers.Any(marker => marker.Direction == ExportTemplateMarkerDirection.Column))
                .Select(item => item.Cell.Address.ColumnNumber)
                .Distinct()
                .OrderDescending()
                .ToArray();
            if (matrixCells.Length > 0 && (rowNumbers.Length > 0 || columnNumbers.Length > 0))
                throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    $"工作表“{worksheet.Name}”中的矩阵区域不能与行循环或列循环混用。");
            if (rowNumbers.Length > 0 && columnNumbers.Length > 0)
                throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    $"工作表“{worksheet.Name}”不能同时进行行循环和列循环。");

            foreach (var matrixCell in matrixCells)
                itemCount = Math.Max(itemCount, ExpandMatrix(worksheet, matrixCell.Cell, request, cancellationToken));
            foreach (var rowNumber in rowNumbers)
                itemCount = Math.Max(itemCount, ExpandRow(worksheet, rowNumber, request, cancellationToken));
            foreach (var columnNumber in columnNumbers)
                itemCount = Math.Max(itemCount, ExpandColumn(worksheet, columnNumber, request, cancellationToken));

            foreach (var item in FindMarkerCells(worksheet).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var markers = ExportTemplateMarkers.Parse(item.Cell.GetString());
                if (markers.Any(marker => marker.Collection is not null))
                    throw new ExportHandlerException(
                        "EXPORT_TEMPLATE_MARKER_INVALID",
                        $"工作表“{worksheet.Name}”中存在未展开的循环标记。");
                ReplaceScalarMarkers(item.Cell, request);
            }
        }

        if (request.Template.Documents.Count > 0)
            throw new InvalidOperationException("XLSX 模板暂不支持 document 绑定。");

        workbook.SaveAs(context.OutputPath);
        return new ExportRenderResult(itemCount);
    }

    private static int ExpandRow(
        IXLWorksheet worksheet,
        int rowNumber,
        ExportRequest request,
        CancellationToken cancellationToken)
    {
        var row = worksheet.Row(rowNumber);
        var markers = row.CellsUsed()
            .SelectMany(cell => ExportTemplateMarkers.Parse(cell.GetString()))
            .Where(marker => marker.Collection is not null)
            .ToArray();
        var collection = RequireSingleCollection(markers);
        var table = GetTable(request, collection);
        ValidateTable(table, collection);
        if (table.Rows.Count == 0)
        {
            row.Delete();
            return 0;
        }

        if (table.Rows.Count > 1)
            row.InsertRowsBelow(table.Rows.Count - 1);
        for (var rowIndex = 1; rowIndex < table.Rows.Count; rowIndex++)
            row.CopyTo(worksheet.Row(rowNumber + rowIndex));
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceLoopCells(
                worksheet.Row(rowNumber + rowIndex).CellsUsed(),
                request,
                collection,
                rowIndex,
                ExportTemplateMarkerDirection.Row);
        }
        return table.Rows.Count;
    }

    private static int ExpandMatrix(
        IXLWorksheet worksheet,
        IXLCell anchor,
        ExportRequest request,
        CancellationToken cancellationToken)
    {
        var markers = ExportTemplateMarkers.Parse(anchor.GetString());
        if (markers.Count != 1 || markers[0].Direction != ExportTemplateMarkerDirection.Matrix)
            throw new ExportHandlerException(
                "EXPORT_TEMPLATE_MARKER_INVALID",
                "XLSX 矩阵标记必须独占一个单元格，例如 {{items|matrix}}。");
        var collection = markers[0].Collection!;
        var table = GetTable(request, collection);
        ValidateTable(table, collection);
        if (table.Rows.Count == 0)
        {
            anchor.Clear();
            return 0;
        }

        var rowNumber = anchor.Address.RowNumber;
        var columnNumber = anchor.Address.ColumnNumber;
        var templateRow = worksheet.Row(rowNumber);
        if (table.Rows.Count > 1)
            templateRow.InsertRowsBelow(table.Rows.Count - 1);
        for (var rowIndex = 1; rowIndex < table.Rows.Count; rowIndex++)
            templateRow.CopyTo(worksheet.Row(rowNumber + rowIndex));

        if (table.Columns.Count > 1)
            worksheet.Column(columnNumber).InsertColumnsAfter(table.Columns.Count - 1);
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var cell = worksheet.Cell(rowNumber + rowIndex, columnNumber + columnIndex);
                if (columnIndex > 0)
                    anchor.CopyTo(cell);
                var value = table.Rows[rowIndex][columnIndex];
                var type = table.Columns[columnIndex].Type;
                SetCellValue(
                    cell,
                    ExportRequestValidator.NormalizeValue(
                        value,
                        type,
                        table.Columns[columnIndex].Name,
                        rowIndex + 1),
                    ToScalarType(type));
            }
        }
        return table.Rows.Count;
    }

    private static int ExpandColumn(
        IXLWorksheet worksheet,
        int columnNumber,
        ExportRequest request,
        CancellationToken cancellationToken)
    {
        var column = worksheet.Column(columnNumber);
        var markers = column.CellsUsed()
            .SelectMany(cell => ExportTemplateMarkers.Parse(cell.GetString()))
            .Where(marker => marker.Collection is not null)
            .ToArray();
        var collection = RequireSingleCollection(markers);
        var table = GetTable(request, collection);
        ValidateTable(table, collection);
        if (table.Rows.Count == 0)
        {
            column.Delete();
            return 0;
        }

        if (table.Rows.Count > 1)
            column.InsertColumnsAfter(table.Rows.Count - 1);
        for (var columnIndex = 1; columnIndex < table.Rows.Count; columnIndex++)
            column.CopyTo(worksheet.Column(columnNumber + columnIndex));
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceLoopCells(
                worksheet.Column(columnNumber + rowIndex).CellsUsed(),
                request,
                collection,
                rowIndex,
                ExportTemplateMarkerDirection.Column);
        }
        return table.Rows.Count;
    }

    private static void ReplaceLoopCells(
        IEnumerable<IXLCell> cells,
        ExportRequest request,
        string collection,
        int rowIndex,
        ExportTemplateMarkerDirection direction)
    {
        foreach (var cell in cells.ToArray())
        {
            var text = cell.GetString();
            var markers = ExportTemplateMarkers.Parse(text);
            if (markers.Count == 0)
                continue;
            if (markers.Any(marker => marker.Collection is not null && marker.Direction != direction))
                throw new ExportTemplateMarkerException("循环标记方向与样板区域不匹配。");
            var tableMarkers = markers.Where(marker => marker.Collection is not null).ToArray();
            if (tableMarkers.Any(marker => !string.Equals(marker.Collection, collection, StringComparison.Ordinal)))
                throw new ExportTemplateMarkerException("一个循环区域只能使用一个表格绑定。");
            if (tableMarkers.Length == 1 && string.Equals(tableMarkers[0].Raw, text, StringComparison.Ordinal))
            {
                var marker = tableMarkers[0];
                var table = GetTable(request, collection);
                if (!ExportTemplateMarkers.TryGetTableValue(table, marker.Field, rowIndex, out var value, out var type))
                    throw new ExportTemplateMarkerException($"模板标记引用了不存在的表格字段：{collection}.{marker.Field}。");
                try
                {
                    SetCellValue(cell, ExportRequestValidator.NormalizeValue(value, type, marker.Field, rowIndex + 1), ToScalarType(type));
                }
                catch (ExportHandlerException exception) when (exception.Code == "EXPORT_VALUE_INVALID")
                {
                    throw new ExportHandlerException(
                        exception.Code,
                        exception.Message,
                        exception.Retryable,
                        MergeDetails(exception.Details, collection),
                        exception);
                }
                continue;
            }

            cell.SetValue(ExportTemplateMarkers.Replace(text, marker => ResolveMarkerText(
                marker,
                request,
                collection,
                rowIndex,
                direction)));
        }
    }

    private static void ReplaceScalarMarkers(IXLCell cell, ExportRequest request)
    {
        var text = cell.GetString();
        if (ExportTemplateMarkers.Parse(text).Count == 0)
            return;
        cell.SetValue(ExportTemplateMarkers.Replace(text, marker =>
        {
            if (marker.Collection is not null)
                throw new ExportTemplateMarkerException("模板中存在未展开的循环标记。");
            return Convert.ToString(request.Template!.Values.GetValueOrDefault(marker.Field), System.Globalization.CultureInfo.InvariantCulture);
        }));
    }

    private static string ResolveMarkerText(
        ExportTemplateMarker marker,
        ExportRequest request,
        string collection,
        int rowIndex,
        ExportTemplateMarkerDirection direction)
    {
        if (marker.Collection is null)
            return Convert.ToString(request.Template!.Values.GetValueOrDefault(marker.Field), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (!string.Equals(marker.Collection, collection, StringComparison.Ordinal) || marker.Direction != direction)
            throw new ExportTemplateMarkerException("循环标记方向或集合与样板区域不匹配。");
        var table = GetTable(request, collection);
        if (!ExportTemplateMarkers.TryGetTableValue(table, marker.Field, rowIndex, out var value, out var type))
            throw new ExportTemplateMarkerException($"模板标记引用了不存在的表格字段：{collection}.{marker.Field}。");
        return Convert.ToString(ExportRequestValidator.NormalizeValue(value, type, marker.Field, rowIndex + 1), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static IReadOnlyList<MarkerCell> FindMarkerCells(XLWorkbook workbook) =>
        workbook.Worksheets.SelectMany(FindMarkerCells).ToArray();

    private static IReadOnlyList<MarkerCell> FindMarkerCells(IXLWorksheet worksheet) =>
        worksheet.CellsUsed()
            .Select(cell => new MarkerCell(cell, ExportTemplateMarkers.Parse(cell.GetString())))
            .Where(item => item.Markers.Count > 0)
            .ToArray();

    private static void ValidateLoopDirections(
        IEnumerable<MarkerCell> markerCells,
        ICollection<ExportDiagnostic> diagnostics)
    {
        foreach (var group in markerCells.GroupBy(item => item.Cell.Worksheet.Name, StringComparer.Ordinal))
        {
            var hasRow = group.Any(item => item.Markers.Any(marker => marker.Direction == ExportTemplateMarkerDirection.Row));
            var hasColumn = group.Any(item => item.Markers.Any(marker => marker.Direction == ExportTemplateMarkerDirection.Column));
            var hasMatrix = group.Any(item => item.Markers.Any(marker => marker.Direction == ExportTemplateMarkerDirection.Matrix));
            if (hasMatrix && (hasRow || hasColumn))
                diagnostics.Add(new(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    $"工作表“{group.Key}”不能同时进行矩阵展开和行/列循环。"));
            if (hasRow && hasColumn)
                diagnostics.Add(new(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    $"工作表“{group.Key}”不能同时进行行循环和列循环。"));
        }
    }

    private static string RequireSingleCollection(IEnumerable<ExportTemplateMarker> markers)
    {
        var collections = markers.Select(marker => marker.Collection).Distinct(StringComparer.Ordinal).ToArray();
        if (collections.Length != 1 || collections[0] is null)
            throw new ExportTemplateMarkerException("一个循环区域只能使用一个表格绑定。");
        return collections[0]!;
    }

    private static ExportTableContent GetTable(ExportRequest request, string key)
    {
        if (request.Template!.Tables.TryGetValue(key, out var table))
            return table;
        throw new ExportTemplateMarkerException($"缺少模板循环数据：{key}。");
    }

    private static void ValidateTable(ExportTableContent table, string key)
    {
        var validation = ExportRequestValidator.ValidateTableContent(
            table,
            new ExportContentCapabilities(ExportContentKind.Table, [ExportFeature.TypedValues]));
        if (validation is not null)
            throw new ExportHandlerException(
                validation.Code,
                $"XLSX 模板表格绑定“{key}”无效：{validation.Message}",
                details: MergeDetails(validation.Details, key));
    }

    private static IReadOnlyDictionary<string, object?> MergeDetails(
        IReadOnlyDictionary<string, object?>? details,
        string bindingKey)
    {
        var result = details is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(details, StringComparer.Ordinal);
        result["binding_key"] = bindingKey;
        return result;
    }

    private static void ValidateFormulaSafety(
        XLWorkbook workbook,
        ICollection<ExportDiagnostic> diagnostics)
    {
        foreach (var worksheet in workbook.Worksheets)
        {
            foreach (var cell in worksheet.CellsUsed(cell => cell.HasFormula))
            {
                var formula = cell.FormulaA1;
                if (!ExternalWorkbookReference.IsMatch(formula)
                    && !DangerousFormula.IsMatch(formula)
                    && !(formula.Contains('|', StringComparison.Ordinal)
                        && formula.Contains('!', StringComparison.Ordinal)))
                    continue;
                diagnostics.Add(new(
                    "EXPORT_TEMPLATE_UNSUPPORTED",
                    "XLSX 模板包含外部工作簿引用或危险公式。",
                    $"{worksheet.Name}!{cell.Address}"));
            }
        }
    }

    private static ExportScalarType ToScalarType(ExportColumnType type) => type switch
    {
        ExportColumnType.Text => ExportScalarType.Text,
        ExportColumnType.Integer => ExportScalarType.Integer,
        ExportColumnType.Decimal => ExportScalarType.Decimal,
        ExportColumnType.Date => ExportScalarType.Date,
        ExportColumnType.Time => ExportScalarType.Time,
        ExportColumnType.Duration => ExportScalarType.Duration,
        ExportColumnType.DateTime => ExportScalarType.DateTime,
        ExportColumnType.Boolean => ExportScalarType.Boolean,
        _ => ExportScalarType.Text,
    };

    private static void SetCellValue(IXLCell cell, object? value, ExportScalarType? type)
    {
        if (value is null)
        {
            cell.Clear();
            return;
        }
        if (value is System.Text.Json.JsonElement element)
            value = element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString()
                : element.ToString();
        var text = value?.ToString() ?? string.Empty;
        switch (type)
        {
            case ExportScalarType.Integer:
                cell.Value = Convert.ToInt32(value);
                break;
            case ExportScalarType.Decimal:
                cell.Value = Convert.ToDecimal(value);
                break;
            case ExportScalarType.Boolean:
                cell.Value = Convert.ToBoolean(value);
                break;
            case ExportScalarType.Date:
                cell.Value = value is DateOnly date ? date.ToDateTime(TimeOnly.MinValue) : DateTime.Parse(text);
                break;
            case ExportScalarType.Time:
                cell.Value = value is TimeOnly time ? time.ToTimeSpan() : TimeSpan.Parse(text);
                break;
            case ExportScalarType.Duration:
                cell.Value = value is TimeSpan duration ? duration : TimeSpan.Parse(text);
                break;
            case ExportScalarType.DateTime:
                cell.Value = value is DateTimeOffset dateTime ? dateTime.LocalDateTime : DateTime.Parse(text);
                break;
            default:
                cell.SetValue(text);
                break;
        }
    }

    private static ExportTemplateValidationResult Invalid(
        string code,
        string message) =>
        new(false, null, null, null, null, [], [], [new ExportDiagnostic(code, message)]);

    private sealed record MarkerCell(IXLCell Cell, IReadOnlyList<ExportTemplateMarker> Markers);

    private sealed class ExportTemplateMarkerException(string message) : InvalidOperationException(message);
}
