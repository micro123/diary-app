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
    private const string MetadataSheetName = "__diary_template";
    private const string Marker = "diary.export.template";
    private static readonly Regex DangerousFormula = new(
        "(?:^|[^A-Z])(?:WEBSERVICE|FILTERXML|HYPERLINK|RTD|DDE)\\s*\\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExternalWorkbookReference = new(
        "\\[[^\\]]+\\][^!]+!",
        RegexOptions.Compiled);
    private static readonly HashSet<string> ScalarTypes = Enum.GetNames<ExportScalarType>()
        .Select(value => value.ToLowerInvariant())
        .ToHashSet(StringComparer.Ordinal);

    public string PluginId => "xlsx";
    public string FormatId => "xlsx";
    public IReadOnlyList<string> SupportedTemplateExtensions => [".xlsx"];

    public ValueTask<ExportTemplateValidationResult> ValidateAsync(
        Stream templateStream,
        ExportTemplateValidationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<ExportDiagnostic>();
        if (!string.Equals(context.FileExtension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new("EXPORT_TEMPLATE_EXTENSION_INVALID", "XLSX 模板扩展名必须为 .xlsx。"));
            return ValueTask.FromResult(Invalid(diagnostics));
        }

        try
        {
            diagnostics.AddRange(OpenXmlTemplateSafety.ValidatePackage(templateStream));
            if (diagnostics.Count > 0)
                return ValueTask.FromResult(Invalid(diagnostics));

            templateStream.Position = 0;
            using var workbook = new XLWorkbook(templateStream);
            if (!workbook.Worksheets.TryGetWorksheet(MetadataSheetName, out var metadata))
            {
                diagnostics.Add(new("EXPORT_TEMPLATE_METADATA_MISSING", $"缺少模板元数据工作表“{MetadataSheetName}”。"));
                return ValueTask.FromResult(Invalid(diagnostics));
            }

            if (!string.Equals(metadata.Cell("A1").GetString(), Marker, StringComparison.Ordinal))
                diagnostics.Add(new("EXPORT_TEMPLATE_MARKER_INVALID", "模板元数据标记无效。"));

            var templateName = metadata.Cell("A2").GetString().Trim();
            var version = metadata.Cell("A3").GetString().Trim();
            var displayName = metadata.Cell("A4").GetString().Trim();
            var description = metadata.Cell("A5").GetString().Trim();
            if (string.IsNullOrWhiteSpace(templateName))
                diagnostics.Add(new("EXPORT_TEMPLATE_NAME_MISSING", "模板名不能为空。"));
            if (string.IsNullOrWhiteSpace(version))
                diagnostics.Add(new("EXPORT_TEMPLATE_VERSION_MISSING", "模板版本不能为空。"));

            var bindings = ReadBindings(metadata, diagnostics);
            ValidateFormulaSafety(workbook, diagnostics);
            if (diagnostics.Count > 0)
                return ValueTask.FromResult(new ExportTemplateValidationResult(
                    false,
                    templateName,
                    displayName,
                    description,
                    version,
                    bindings,
                    [],
                    diagnostics));

            return ValueTask.FromResult(new ExportTemplateValidationResult(
                true,
                templateName,
                string.IsNullOrWhiteSpace(displayName) ? templateName : displayName,
                description,
                version,
                bindings,
                [ExportFeature.TypedValues, ExportFeature.BasicStyle, ExportFeature.MergeCells],
                []));
        }
        catch (Exception exception)
        {
            diagnostics.Add(new("EXPORT_TEMPLATE_STRUCTURE_INVALID", $"无法读取 XLSX 模板：{exception.Message}"));
            return ValueTask.FromResult(Invalid(diagnostics));
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
        var metadata = workbook.Worksheet(MetadataSheetName);
        var bindings = ReadBindingTargets(metadata, cancellationToken);
        var itemCount = 0;

        foreach (var (key, value) in request.Template.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bindings.TryGetValue(key, out var binding) || binding.Kind != ExportBindingKind.Scalar)
                continue;
            var cell = ResolveCell(workbook, binding.Target);
            SetCellValue(cell, value, binding.ScalarType);
        }

        foreach (var (key, table) in request.Template.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bindings.TryGetValue(key, out var binding) || binding.Kind != ExportBindingKind.Table)
                continue;
            var start = ResolveCell(workbook, binding.Target);
            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
                start.Worksheet.Cell(start.Address.RowNumber, start.Address.ColumnNumber + columnIndex)
                    .SetValue(table.Columns[columnIndex].Name);
            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
                {
                    var cell = start.Worksheet.Cell(
                        start.Address.RowNumber + rowIndex + 1,
                        start.Address.ColumnNumber + columnIndex);
                    SetCellValue(cell, table.Rows[rowIndex][columnIndex], ToScalarType(table.Columns[columnIndex].Type));
                }
                itemCount++;
            }
        }

        if (request.Template.Documents.Count > 0)
            throw new InvalidOperationException("XLSX 模板暂不支持 document 绑定。");

        workbook.SaveAs(context.OutputPath);
        return new ExportRenderResult(itemCount);
    }

    private static IReadOnlyList<ExportBindingDescriptor> ReadBindings(
        IXLWorksheet metadata,
        ICollection<ExportDiagnostic> diagnostics)
    {
        var result = new List<ExportBindingDescriptor>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var lastRow = metadata.LastRowUsed()?.RowNumber() ?? 7;
        for (var row = 8; row <= lastRow; row++)
        {
            var key = metadata.Cell(row, 1).GetString().Trim();
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (!keys.Add(key))
            {
                diagnostics.Add(new("EXPORT_TEMPLATE_BINDING_DUPLICATE", "模板绑定键重复。", key));
                continue;
            }

            var kindText = metadata.Cell(row, 2).GetString().Trim().ToLowerInvariant();
            var scalarText = metadata.Cell(row, 3).GetString().Trim().ToLowerInvariant();
            var requiredText = metadata.Cell(row, 4).GetString().Trim();
            var defaultText = metadata.Cell(row, 5).GetString();
            var target = metadata.Cell(row, 6).GetString().Trim();
            var description = metadata.Cell(row, 7).GetString().Trim();
            if (!Enum.TryParse<ExportBindingKind>(kindText, true, out var kind)
                || string.IsNullOrWhiteSpace(target))
            {
                diagnostics.Add(new("EXPORT_TEMPLATE_BINDING_INVALID", "模板绑定类型或目标地址无效。", key));
                continue;
            }
            ExportScalarType? scalarType = null;
            if (kind == ExportBindingKind.Scalar)
            {
                if (!ScalarTypes.Contains(scalarText)
                    || !Enum.TryParse<ExportScalarType>(scalarText, true, out var parsed))
                {
                    diagnostics.Add(new("EXPORT_TEMPLATE_BINDING_TYPE_INVALID", "标量绑定类型无效。", key));
                    continue;
                }
                scalarType = parsed;
            }

            var required = !bool.TryParse(requiredText, out var parsedRequired) || parsedRequired;
            var hasDefault = !string.IsNullOrWhiteSpace(defaultText);
            object? defaultValue = hasDefault ? ParseScalar(defaultText, scalarType) : null;
            result.Add(new ExportBindingDescriptor(key, kind, scalarType, required, hasDefault, defaultValue, description));
        }
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

    private static Dictionary<string, BindingTarget> ReadBindingTargets(IXLWorksheet metadata, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, BindingTarget>(StringComparer.Ordinal);
        var lastRow = metadata.LastRowUsed()?.RowNumber() ?? 7;
        for (var row = 8; row <= lastRow; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = metadata.Cell(row, 1).GetString().Trim();
            if (string.IsNullOrWhiteSpace(key))
                continue;
            var kind = Enum.Parse<ExportBindingKind>(metadata.Cell(row, 2).GetString().Trim(), true);
            var scalar = Enum.TryParse<ExportScalarType>(metadata.Cell(row, 3).GetString().Trim(), true, out var parsed)
                ? parsed
                : (ExportScalarType?)null;
            result[key] = new(kind, scalar, metadata.Cell(row, 6).GetString().Trim());
        }
        return result;
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

    private static IXLCell ResolveCell(XLWorkbook workbook, string target)
    {
        var separator = target.IndexOf('!', StringComparison.Ordinal);
        if (separator < 0)
            return workbook.Worksheet("明细").Cell(target);
        var sheetName = target[..separator];
        var address = target[(separator + 1)..];
        return workbook.Worksheet(sheetName).Cell(address);
    }

    private static object? ParseScalar(string value, ExportScalarType? type) => type switch
    {
        ExportScalarType.Integer when int.TryParse(value, out var integer) => integer,
        ExportScalarType.Decimal when decimal.TryParse(value, out var decimalValue) => decimalValue,
        ExportScalarType.Boolean when bool.TryParse(value, out var boolean) => boolean,
        ExportScalarType.Date when DateOnly.TryParse(value, out var date) => date,
        ExportScalarType.Time when TimeOnly.TryParse(value, out var time) => time,
        ExportScalarType.Duration when TimeSpan.TryParse(value, out var duration) => duration,
        ExportScalarType.DateTime when DateTimeOffset.TryParse(value, out var dateTime) => dateTime,
        _ => value,
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
                cell.Style.DateFormat.Format = "yyyy-mm-dd";
                break;
            case ExportScalarType.Time:
                cell.Value = value is TimeOnly time ? time.ToTimeSpan() : TimeSpan.Parse(text);
                cell.Style.DateFormat.Format = "hh:mm:ss";
                break;
            case ExportScalarType.Duration:
                cell.Value = value is TimeSpan duration ? duration : TimeSpan.Parse(text);
                cell.Style.NumberFormat.Format = "[h]:mm:ss";
                break;
            case ExportScalarType.DateTime:
                cell.Value = value is DateTimeOffset dateTime ? dateTime.LocalDateTime : DateTime.Parse(text);
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                break;
            default:
                cell.SetValue(text);
                break;
        }
    }

    private static ExportTemplateValidationResult Invalid(IReadOnlyList<ExportDiagnostic> diagnostics) =>
        new(false, null, null, null, null, [], [], diagnostics);

    private sealed record BindingTarget(
        ExportBindingKind Kind,
        ExportScalarType? ScalarType,
        string Target);
}
