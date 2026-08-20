using System.Globalization;
using System.IO.Compression;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using System.Text.RegularExpressions;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Diary.ScriptHost;

namespace Diary.Export.Docx;

public sealed class DocxExportPlugin : IExportPlugin
{
    private readonly DocxExportHandler _handler = new();
    private readonly DocxTemplateHandler _templateHandler = new();

    public ExportPluginManifest Manifest { get; } = new("docx", "1.0.0");

    public IEnumerable<IExportHandler> GetExportHandlers() => [_handler];

    public IEnumerable<IExportTemplateHandler> GetTemplateHandlers() => [_templateHandler];
}

internal sealed class DocxExportHandler : IExportHandler
{
    public ExportFormatDescriptor Descriptor { get; } = new(
        "docx",
        "Word 文档",
        ".docx",
        [".docx"],
        [
            new ExportContentCapabilities(
                ExportContentKind.Table,
                [
                    ExportFeature.UnicodeText,
                    ExportFeature.TypedValues,
                    ExportFeature.BasicStyle,
                    ExportFeature.MergeCells,
                    ExportFeature.GeneratedAggregate,
                ]),
            new ExportContentCapabilities(
                ExportContentKind.Document,
                [
                    ExportFeature.UnicodeText,
                    ExportFeature.TypedValues,
                    ExportFeature.BasicStyle,
                    ExportFeature.Paragraphs,
                    ExportFeature.DocumentTables,
                    ExportFeature.MergeCells,
                    ExportFeature.GeneratedAggregate,
                ]),
        ],
        SupportsTemplates: true,
        FormatOptions: []);

    public ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.FormatOptions is not null)
            throw new ExportHandlerException("EXPORT_INVALID_REQUEST", "DOCX 不支持格式选项。");

        using var document = WordprocessingDocument.Create(
            context.OutputPath,
            WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new W.Document(new W.Body());
        var body = mainPart.Document.Body!;

        var itemCount = request.Content switch
        {
            ExportTableContent table => RenderTableDocument(body, table, cancellationToken),
            ExportDocumentContent content => RenderDocument(body, content, cancellationToken),
            _ => throw new InvalidOperationException("DOCX 导出只支持 table 或 document 内容。"),
        };
        mainPart.Document.Save();
        return ValueTask.FromResult(new ExportRenderResult(itemCount));
    }

    private static int RenderDocument(
        W.Body body,
        ExportDocumentContent document,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(document.Title))
            body.Append(CreateParagraph(
                document.Title,
                bold: true,
                fontSize: document.Style switch
                {
                    ExportTableStyle.Compact => 26,
                    ExportTableStyle.Report => 36,
                    _ => 32,
                },
                center: document.Style != ExportTableStyle.Compact));

        foreach (var block in document.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (block)
            {
                case ExportHeadingBlock heading:
                    body.Append(CreateParagraph(
                        heading.Text,
                        bold: true,
                        fontSize: HeadingFontSize(heading.Level, document.Style)));
                    break;
                case ExportParagraphBlock paragraph:
                    body.Append(CreateParagraph(
                        paragraph.Text,
                        fontSize: document.Style == ExportTableStyle.Compact ? 18 : null));
                    break;
                case ExportTableBlock table:
                    var tableContent = table.Table.Style == ExportTableStyle.Default
                        && document.Style != ExportTableStyle.Default
                            ? table.Table with { Style = document.Style }
                            : table.Table;
                    body.Append(CreateTable(tableContent, cancellationToken));
                    break;
                default:
                    throw new ExportHandlerException("EXPORT_INVALID_REQUEST", "DOCX 文档包含未知文档块。");
            }
        }
        return document.Blocks.Count;
    }

    private static int RenderTableDocument(
        W.Body body,
        ExportTableContent table,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(table.Title))
            body.Append(CreateParagraph(
                table.Title,
                bold: true,
                fontSize: table.Style == ExportTableStyle.Compact ? 24 : 30,
                center: table.Style != ExportTableStyle.Compact));
        body.Append(CreateTable(table, cancellationToken));
        return table.Rows.Count;
    }

    private static W.Table CreateTable(ExportTableContent source, CancellationToken cancellationToken)
    {
        var table = new W.Table();
        table.AppendChild(new W.TableProperties(
            new W.TableBorders(
                Border<W.TopBorder>(),
                Border<W.LeftBorder>(),
                Border<W.BottomBorder>(),
                Border<W.RightBorder>(),
                Border<W.InsideHorizontalBorder>(),
                Border<W.InsideVerticalBorder>()),
            new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" }));

        var header = new W.TableRow();
        foreach (var column in source.Columns)
            header.Append(CreateCell(column.Name, bold: true, style: source.Style, header: true));
        table.Append(header);

        var covered = BuildMergeMap(source);
        for (var rowIndex = 0; rowIndex < source.Rows.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new W.TableRow();
            for (var columnIndex = 0; columnIndex < source.Columns.Count; columnIndex++)
            {
                var merge = covered[rowIndex, columnIndex];
                if (merge?.Skip == true)
                    continue;
                var column = source.Columns[columnIndex];
                var normalized = ExportRequestValidator.NormalizeValue(
                    source.Rows[rowIndex][columnIndex],
                    column.Type,
                    column.Name,
                    rowIndex + 1);
                row.Append(CreateCell(
                    FormatValue(normalized, column),
                    gridSpan: merge?.ColumnSpan ?? 1,
                    verticalMerge: merge?.VerticalMerge,
                    style: source.Style));
            }
            table.Append(row);
        }

        if (source.Aggregates.Count > 0)
        {
            var values = new string[source.Columns.Count];
            var labelColumnIndex = ExportRequestValidator.GetAggregateLabelColumnIndex(source);
            values[labelColumnIndex] = ExportRequestValidator.GetAggregateLabel(source);
            foreach (var aggregate in source.Aggregates)
            {
                var columnIndex = source.Columns
                    .Select((column, index) => (column, index))
                    .First(item => string.Equals(item.column.Name, aggregate.ColumnName, StringComparison.OrdinalIgnoreCase)).index;
                values[columnIndex] = FormatAggregate(source, columnIndex);
            }
            var totalRow = new W.TableRow();
            foreach (var value in values)
                totalRow.Append(CreateCell(value, bold: true, style: source.Style, total: true));
            table.Append(totalRow);
        }
        return table;
    }

    private static MergeCell?[,] BuildMergeMap(ExportTableContent table)
    {
        var result = new MergeCell?[table.Rows.Count, table.Columns.Count];
        foreach (var merge in table.Merges)
        {
            var startRow = merge.StartRow - 1;
            var startColumn = merge.StartColumn - 1;
            for (var rowOffset = 0; rowOffset < merge.RowSpan; rowOffset++)
            {
                result[startRow + rowOffset, startColumn] = new MergeCell(
                    Skip: false,
                    ColumnSpan: merge.ColumnSpan,
                    VerticalMerge: merge.RowSpan > 1
                        ? rowOffset == 0 ? W.MergedCellValues.Restart : W.MergedCellValues.Continue
                        : null);
                for (var columnOffset = 1; columnOffset < merge.ColumnSpan; columnOffset++)
                    result[startRow + rowOffset, startColumn + columnOffset] = new MergeCell(true, 1, null);
            }
        }
        return result;
    }

    private static W.TableCell CreateCell(
        string? text,
        bool bold = false,
        int gridSpan = 1,
        W.MergedCellValues? verticalMerge = null,
        ExportTableStyle style = ExportTableStyle.Default,
        bool header = false,
        bool total = false)
    {
        var properties = new W.TableCellProperties();
        if (gridSpan > 1)
            properties.Append(new W.GridSpan { Val = gridSpan });
        if (verticalMerge is not null)
            properties.Append(new W.VerticalMerge { Val = verticalMerge });
        if (style == ExportTableStyle.Report && (header || total))
            properties.Append(new W.Shading
            {
                Val = W.ShadingPatternValues.Clear,
                Fill = header ? "4472C4" : "D9EAF7",
            });
        return new W.TableCell(properties, CreateParagraph(
            text ?? string.Empty,
            bold,
            fontSize: style == ExportTableStyle.Compact ? 18 : null,
            color: style == ExportTableStyle.Report && header ? "FFFFFF" : null));
    }

    private static W.Paragraph CreateParagraph(
        string text,
        bool bold = false,
        int? fontSize = null,
        bool center = false,
        string? color = null)
    {
        var runProperties = new W.RunProperties();
        if (bold)
            runProperties.Append(new W.Bold());
        if (fontSize is not null)
            runProperties.Append(new W.FontSize { Val = fontSize.Value.ToString(CultureInfo.InvariantCulture) });
        if (color is not null)
            runProperties.Append(new W.Color { Val = color });
        var run = new W.Run(runProperties, new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
        var paragraphProperties = center
            ? new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center })
            : null;
        return paragraphProperties is null
            ? new W.Paragraph(run)
            : new W.Paragraph(paragraphProperties, run);
    }

    private static int HeadingFontSize(int level, ExportTableStyle style)
    {
        var baseSize = level switch { 1 => 30, 2 => 26, _ => 22 };
        return style switch
        {
            ExportTableStyle.Compact => baseSize - 4,
            ExportTableStyle.Report => baseSize + 2,
            _ => baseSize,
        };
    }

    private static TBorder Border<TBorder>() where TBorder : W.BorderType, new() =>
        new() { Val = W.BorderValues.Single, Size = 4 };

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
                .Select((row, index) => ExportRequestValidator.NormalizeValue(
                    row[columnIndex], type, table.Columns[columnIndex].Name, index + 1))
                .OfType<TimeSpan>()
                .Aggregate(TimeSpan.Zero, (total, item) => total + item);
            return duration.ToString("c", CultureInfo.InvariantCulture);
        }

        var total = table.Rows
            .Select((row, index) => ExportRequestValidator.NormalizeValue(
                row[columnIndex], type, table.Columns[columnIndex].Name, index + 1))
            .Select(value => Convert.ToDecimal(value, CultureInfo.InvariantCulture))
            .Sum();
        return total.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record MergeCell(bool Skip, int ColumnSpan, W.MergedCellValues? VerticalMerge);
}

internal sealed class DocxTemplateHandler : IExportTemplateHandler
{
    private const string WordprocessingNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly Regex DangerousFieldInstruction = new(
        "\\b(?:DDEAUTO|DDE|INCLUDETEXT|INCLUDEPICTURE|LINK|DATABASE)\\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string PluginId => "docx";
    public string FormatId => "docx";
    public IReadOnlyList<string> SupportedTemplateExtensions => [".docx"];

    public ValueTask<ExportTemplateValidationResult> ValidateAsync(
        Stream templateStream,
        ExportTemplateValidationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(context.FileExtension, ".docx", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(Invalid("EXPORT_TEMPLATE_EXTENSION_INVALID", "DOCX 模板扩展名必须为 .docx。"));
        try
        {
            var diagnostics = OpenXmlTemplateSafety.ValidatePackage(templateStream).ToList();
            if (diagnostics.Count > 0)
                return ValueTask.FromResult(new ExportTemplateValidationResult(
                    false,
                    null,
                    null,
                    null,
                    null,
                    [],
                    [],
                    diagnostics));

            if (ContainsDangerousFieldInstruction(templateStream))
                return ValueTask.FromResult(Invalid(
                    "EXPORT_TEMPLATE_UNSUPPORTED",
                    "DOCX 模板包含可能访问外部资源的字段指令。"));

            templateStream.Position = 0;
            using var document = WordprocessingDocument.Open(templateStream, false);
            var mainPart = document.MainDocumentPart
                ?? throw new InvalidDataException("DOCX 缺少主文档部件。");
            var documentRoot = mainPart.Document
                ?? throw new InvalidDataException("DOCX 主文档为空。");
            var markers = GetMarkers(documentRoot).ToArray();
            if (markers.Length == 0)
                return ValueTask.FromResult(Invalid(
                    "EXPORT_TEMPLATE_MARKER_MISSING",
                    "DOCX 模板至少需要包含一个 {{变量}}、{{items.字段}} 或 {{items.字段|column}} 标记。"));

            ValidateLoopRegions(documentRoot, diagnostics);
            var name = ExportTemplateMarkers.CreateTemplateName(context.FileName);
            var displayName = Path.GetFileNameWithoutExtension(context.FileName);
            if (diagnostics.Count > 0)
                return ValueTask.FromResult(new ExportTemplateValidationResult(
                    false,
                    name,
                    displayName,
                    "使用简易标记的 DOCX 模板。",
                    "1.0.0",
                    ExportTemplateMarkers.InferBindings(markers),
                    [],
                    diagnostics));

            return ValueTask.FromResult(new ExportTemplateValidationResult(
                true,
                name,
                displayName,
                "使用简易标记的 DOCX 模板。",
                "1.0.0",
                ExportTemplateMarkers.InferBindings(markers),
                [ExportFeature.UnicodeText, ExportFeature.BasicStyle, ExportFeature.Paragraphs, ExportFeature.DocumentTables],
                []));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Invalid("EXPORT_TEMPLATE_STRUCTURE_INVALID", $"无法读取 DOCX 模板：{exception.Message}"));
        }
    }

    public async ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Template is null)
            throw new InvalidOperationException("DOCX 模板导出请求缺少 template。");
        await using var input = await context.OpenTemplateAsync(cancellationToken);
        await using (var output = File.Create(context.OutputPath))
            await input.CopyToAsync(output, cancellationToken);
        using var document = WordprocessingDocument.Open(context.OutputPath, true);
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidDataException("DOCX 缺少主文档部件。");
        var documentRoot = mainPart.Document
            ?? throw new InvalidDataException("DOCX 主文档为空。");

        var count = ExpandMatrices(documentRoot, request, cancellationToken);
        count = Math.Max(count, ExpandRows(documentRoot, request, cancellationToken));
        count = Math.Max(count, ExpandColumns(documentRoot, request, cancellationToken));
        foreach (var text in documentRoot.Descendants<W.Text>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = text.Text ?? string.Empty;
            var markers = ExportTemplateMarkers.Parse(original);
            if (markers.Any(marker => marker.Collection is not null))
                throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    "DOCX 模板中存在未展开的循环标记。");
            text.Text = ExportTemplateMarkers.Replace(original, marker => ResolveScalar(request, marker));
        }
        mainPart.Document.Save();
        return new ExportRenderResult(count);
    }

    private static int ExpandRows(
        W.Document document,
        ExportRequest request,
        CancellationToken cancellationToken)
    {
        var itemCount = 0;
        var rows = document.Descendants<W.TableRow>()
            .Where(row => GetMarkers(row).Any(marker => marker.Direction == ExportTemplateMarkerDirection.Row))
            .ToArray();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markers = GetMarkers(row)
                .Where(marker => marker.Direction == ExportTemplateMarkerDirection.Row)
                .ToArray();
            var collection = RequireSingleCollection(markers);
            var table = GetTable(request, collection);
            ValidateTable(table, collection);
            itemCount = Math.Max(itemCount, table.Rows.Count);
            var template = (W.TableRow)row.CloneNode(true);
            if (table.Rows.Count == 0)
            {
                row.Remove();
                continue;
            }

            ReplaceLoopText(row, request, collection, 0, ExportTemplateMarkerDirection.Row);
            OpenXmlElement last = row;
            for (var rowIndex = 1; rowIndex < table.Rows.Count; rowIndex++)
            {
                var clone = (W.TableRow)template.CloneNode(true);
                ReplaceLoopText(clone, request, collection, rowIndex, ExportTemplateMarkerDirection.Row);
                last.InsertAfterSelf(clone);
                last = clone;
            }
        }
        return itemCount;
    }

    private static int ExpandMatrices(
        W.Document document,
        ExportRequest request,
        CancellationToken cancellationToken)
    {
        var itemCount = 0;
        var cells = document.Descendants<W.TableCell>()
            .Where(cell => GetMarkers(cell).Any(marker => marker.Direction == ExportTemplateMarkerDirection.Matrix))
            .ToArray();
        foreach (var cell in cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markers = GetMarkers(cell).ToArray();
            if (markers.Length != 1 || markers[0].Direction != ExportTemplateMarkerDirection.Matrix)
                throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    "DOCX 矩阵标记必须独占一个单元格，例如 {{items|matrix}}。");
            var row = cell.Ancestors<W.TableRow>().FirstOrDefault()
                ?? throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    "DOCX 矩阵标记必须放在表格行中。");
            if (row.Elements<W.TableCell>().Count() != 1)
                throw new ExportHandlerException(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    "DOCX 矩阵标记所在的表格行必须只包含一个单元格。");

            var collection = markers[0].Collection!;
            var table = GetTable(request, collection);
            ValidateTable(table, collection);
            itemCount = Math.Max(itemCount, table.Rows.Count);
            var templateRow = (W.TableRow)row.CloneNode(true);
            var templateCell = (W.TableCell)cell.CloneNode(true);
            if (table.Rows.Count == 0)
            {
                row.Remove();
                continue;
            }

            ReplaceMatrixRow(row, templateCell, table, 0, cancellationToken);
            OpenXmlElement last = row;
            for (var rowIndex = 1; rowIndex < table.Rows.Count; rowIndex++)
            {
                var clone = (W.TableRow)templateRow.CloneNode(true);
                ReplaceMatrixRow(clone, templateCell, table, rowIndex, cancellationToken);
                last.InsertAfterSelf(clone);
                last = clone;
            }
        }
        return itemCount;
    }

    private static void ReplaceMatrixRow(
        W.TableRow row,
        W.TableCell templateCell,
        ExportTableContent table,
        int rowIndex,
        CancellationToken cancellationToken)
    {
        var firstCell = row.Elements<W.TableCell>().Single();
        ReplaceMatrixCell(firstCell, table, rowIndex, 0);
        OpenXmlElement last = firstCell;
        for (var columnIndex = 1; columnIndex < table.Columns.Count; columnIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clone = (W.TableCell)templateCell.CloneNode(true);
            ReplaceMatrixCell(clone, table, rowIndex, columnIndex);
            last.InsertAfterSelf(clone);
            last = clone;
        }
    }

    private static void ReplaceMatrixCell(
        W.TableCell cell,
        ExportTableContent table,
        int rowIndex,
        int columnIndex)
    {
        var column = table.Columns[columnIndex];
        var normalized = ExportRequestValidator.NormalizeValue(
            table.Rows[rowIndex][columnIndex],
            column.Type,
            column.Name,
            rowIndex + 1);
        var replacement = Convert.ToString(normalized, CultureInfo.InvariantCulture) ?? string.Empty;
        var textNodes = cell.Descendants<W.Text>().ToArray();
        var replaced = false;
        foreach (var text in textNodes)
        {
            var original = text.Text ?? string.Empty;
            if (!ExportTemplateMarkers.Parse(original).Any(marker => marker.Direction == ExportTemplateMarkerDirection.Matrix))
                continue;
            text.Text = replacement;
            replaced = true;
        }
        if (!replaced)
            throw new ExportHandlerException(
                "EXPORT_TEMPLATE_MARKER_INVALID",
                "DOCX 矩阵模板单元格缺少矩阵标记。");
    }

    private static int ExpandColumns(
        W.Document document,
        ExportRequest request,
        CancellationToken cancellationToken)
    {
        var itemCount = 0;
        var cells = document.Descendants<W.TableCell>()
            .Where(cell => GetMarkers(cell).Any(marker => marker.Direction == ExportTemplateMarkerDirection.Column))
            .ToArray();
        foreach (var cell in cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markers = GetMarkers(cell)
                .Where(marker => marker.Direction == ExportTemplateMarkerDirection.Column)
                .ToArray();
            var collection = RequireSingleCollection(markers);
            var table = GetTable(request, collection);
            ValidateTable(table, collection);
            itemCount = Math.Max(itemCount, table.Rows.Count);
            var template = (W.TableCell)cell.CloneNode(true);
            if (table.Rows.Count == 0)
            {
                cell.Remove();
                continue;
            }

            ReplaceLoopText(cell, request, collection, 0, ExportTemplateMarkerDirection.Column);
            OpenXmlElement last = cell;
            for (var rowIndex = 1; rowIndex < table.Rows.Count; rowIndex++)
            {
                var clone = (W.TableCell)template.CloneNode(true);
                ReplaceLoopText(clone, request, collection, rowIndex, ExportTemplateMarkerDirection.Column);
                last.InsertAfterSelf(clone);
                last = clone;
            }
        }
        return itemCount;
    }

    private static void ReplaceLoopText(
        OpenXmlElement element,
        ExportRequest request,
        string collection,
        int rowIndex,
        ExportTemplateMarkerDirection direction)
    {
        foreach (var text in element.Descendants<W.Text>())
        {
            var original = text.Text ?? string.Empty;
            text.Text = ExportTemplateMarkers.Replace(original, marker =>
            {
                if (marker.Collection is null)
                    return ResolveScalar(request, marker);
                if (!string.Equals(marker.Collection, collection, StringComparison.Ordinal)
                    || marker.Direction != direction)
                    throw new ExportHandlerException(
                        "EXPORT_TEMPLATE_MARKER_INVALID",
                        $"模板标记 {marker.Raw} 的循环方向或集合与所在区域不匹配。");
                var table = GetTable(request, collection);
                if (!ExportTemplateMarkers.TryGetTableValue(table, marker.Field, rowIndex, out var value, out var type))
                    throw new ExportHandlerException(
                        "EXPORT_TEMPLATE_BINDING_INVALID",
                        $"模板标记引用了不存在的表格字段：{collection}.{marker.Field}。");
                var normalized = ExportRequestValidator.NormalizeValue(value, type, marker.Field, rowIndex + 1);
                return Convert.ToString(normalized, CultureInfo.InvariantCulture);
            });
        }
    }

    private static string? ResolveScalar(ExportRequest request, ExportTemplateMarker marker)
    {
        if (marker.Collection is not null)
            throw new ExportHandlerException(
                "EXPORT_TEMPLATE_MARKER_INVALID",
                $"模板中存在未展开的循环标记：{marker.Raw}。");
        return Convert.ToString(
            request.Template!.Values.GetValueOrDefault(marker.Field),
            CultureInfo.InvariantCulture);
    }

    private static IEnumerable<ExportTemplateMarker> GetMarkers(OpenXmlElement element) =>
        element.Descendants<W.Text>()
            .SelectMany(text => ExportTemplateMarkers.Parse(text.Text ?? string.Empty));

    private static void ValidateLoopRegions(
        W.Document document,
        ICollection<ExportDiagnostic> diagnostics)
    {
        foreach (var text in document.Descendants<W.Text>())
        {
            foreach (var marker in ExportTemplateMarkers.Parse(text.Text ?? string.Empty)
                         .Where(marker => marker.Collection is not null))
            {
                var cell = text.Ancestors<W.TableCell>().FirstOrDefault();
                var row = text.Ancestors<W.TableRow>().FirstOrDefault();
                if (cell is null || row is null)
                {
                    diagnostics.Add(new(
                        "EXPORT_TEMPLATE_MARKER_INVALID",
                        $"DOCX 循环标记必须放在表格中：{marker.Raw}。"));
                    continue;
                }
                if (HasMerge(cell))
                    diagnostics.Add(new(
                        "EXPORT_TEMPLATE_UNSUPPORTED",
                        $"DOCX 循环标记不能放在合并单元格中：{marker.Raw}。"));
            }
        }

        foreach (var row in document.Descendants<W.TableRow>())
        {
            var markers = GetMarkers(row).Where(marker => marker.Collection is not null).ToArray();
            var hasRows = markers.Any(marker => marker.Direction == ExportTemplateMarkerDirection.Row);
            var hasColumns = markers.Any(marker => marker.Direction == ExportTemplateMarkerDirection.Column);
            var hasMatrix = markers.Any(marker => marker.Direction == ExportTemplateMarkerDirection.Matrix);
            if (hasMatrix && (hasRows || hasColumns))
                diagnostics.Add(new(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    "同一个 DOCX 表格行不能同时进行矩阵展开和行/列循环。"));
            if (hasRows && hasColumns)
                diagnostics.Add(new(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    "同一个 DOCX 表格行不能同时进行行循环和列循环。"));
            if (hasRows)
                ValidateSingleCollection(markers.Where(marker => marker.Direction == ExportTemplateMarkerDirection.Row), diagnostics);
        }

        foreach (var cell in document.Descendants<W.TableCell>())
        {
            var matrixMarkers = GetMarkers(cell)
                .Where(marker => marker.Direction == ExportTemplateMarkerDirection.Matrix)
                .ToArray();
            if (matrixMarkers.Length > 0
                && (matrixMarkers.Length != 1 || cell.Ancestors<W.TableRow>().FirstOrDefault()?.Elements<W.TableCell>().Count() != 1))
                diagnostics.Add(new(
                    "EXPORT_TEMPLATE_MARKER_INVALID",
                    "DOCX 矩阵标记必须独占一个仅含该单元格的表格行。"));
            var markers = GetMarkers(cell)
                .Where(marker => marker.Direction == ExportTemplateMarkerDirection.Column)
                .ToArray();
            if (markers.Length > 0)
                ValidateSingleCollection(markers, diagnostics);
        }
    }

    private static void ValidateSingleCollection(
        IEnumerable<ExportTemplateMarker> markers,
        ICollection<ExportDiagnostic> diagnostics)
    {
        if (markers.Select(marker => marker.Collection).Distinct(StringComparer.Ordinal).Count() > 1)
            diagnostics.Add(new(
                "EXPORT_TEMPLATE_MARKER_INVALID",
                "一个循环区域只能使用一个表格绑定。"));
    }

    private static bool HasMerge(W.TableCell cell)
    {
        var properties = cell.TableCellProperties;
        return properties?.GetFirstChild<W.GridSpan>()?.Val?.Value > 1
            || properties?.GetFirstChild<W.VerticalMerge>() is not null
            || properties?.GetFirstChild<W.HorizontalMerge>() is not null;
    }

    private static string RequireSingleCollection(IEnumerable<ExportTemplateMarker> markers)
    {
        var collections = markers.Select(marker => marker.Collection).Distinct(StringComparer.Ordinal).ToArray();
        if (collections.Length != 1 || collections[0] is null)
            throw new ExportHandlerException(
                "EXPORT_TEMPLATE_MARKER_INVALID",
                "一个循环区域只能使用一个表格绑定。");
        return collections[0]!;
    }

    private static ExportTableContent GetTable(ExportRequest request, string key)
    {
        if (request.Template!.Tables.TryGetValue(key, out var table))
            return table;
        throw new ExportHandlerException(
            "EXPORT_TEMPLATE_REQUIRED_BINDING_MISSING",
            $"缺少模板循环数据：{key}。");
    }

    private static void ValidateTable(ExportTableContent table, string key)
    {
        var validation = ExportRequestValidator.ValidateTableContent(
            table,
            new ExportContentCapabilities(ExportContentKind.Table, [ExportFeature.TypedValues]));
        if (validation is not null)
            throw new ExportHandlerException(
                validation.Code,
                $"DOCX 模板表格绑定“{key}”无效：{validation.Message}",
                details: validation.Details);
    }

    private static bool ContainsDangerousFieldInstruction(Stream templateStream)
    {
        var originalPosition = templateStream.Position;
        try
        {
            templateStream.Position = 0;
            using var archive = new ZipArchive(templateStream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in archive.Entries.Where(entry =>
                         entry.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase)
                         && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                using var partStream = entry.Open();
                using var reader = XmlReader.Create(partStream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    IgnoreComments = true,
                });
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                        continue;
                    string? instruction = reader.LocalName switch
                    {
                        "fldSimple" => reader.GetAttribute("instr", WordprocessingNamespace),
                        "instrText" => reader.ReadElementContentAsString(),
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(instruction)
                        && DangerousFieldInstruction.IsMatch(instruction))
                        return true;
                }
            }
            return false;
        }
        finally
        {
            templateStream.Position = originalPosition;
        }
    }

    private static ExportTemplateValidationResult Invalid(string code, string message) =>
        new(false, null, null, null, null, [], [], [new ExportDiagnostic(code, message)]);
}
