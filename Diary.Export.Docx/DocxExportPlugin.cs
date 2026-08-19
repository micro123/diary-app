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
    private static readonly System.Text.RegularExpressions.Regex Placeholder = new(
        "\\{\\{(?<key>[a-z][a-z0-9]*(?:_[a-z0-9]+)*)\\}\\}",
        System.Text.RegularExpressions.RegexOptions.Compiled);
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
        if (!string.Equals(context.FileExtension, ".docx", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(Invalid("EXPORT_TEMPLATE_EXTENSION_INVALID", "DOCX 模板扩展名必须为 .docx。"));
        try
        {
            var diagnostics = OpenXmlTemplateSafety.ValidatePackage(templateStream);
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
            var text = string.Join("\n", documentRoot.Descendants<W.Text>().Select(item => item.Text));
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 3 || !string.Equals(lines[0], "[[diary.export.template]]", StringComparison.Ordinal))
                return ValueTask.FromResult(Invalid("EXPORT_TEMPLATE_METADATA_MISSING", "DOCX 模板缺少元数据头。"));
            var name = MetadataValue(lines, "template_name");
            var version = MetadataValue(lines, "version");
            var bindings = Placeholder.Matches(text)
                .Select(match => match.Groups["key"].Value)
                .Distinct(StringComparer.Ordinal)
                .Select(key => new ExportBindingDescriptor(key, ExportBindingKind.Scalar, ExportScalarType.Text))
                .ToArray();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
                return ValueTask.FromResult(Invalid("EXPORT_TEMPLATE_METADATA_INVALID", "DOCX 模板名和版本不能为空。"));
            return ValueTask.FromResult(new ExportTemplateValidationResult(
                true,
                name,
                name,
                null,
                version,
                bindings,
                [ExportFeature.UnicodeText, ExportFeature.BasicStyle, ExportFeature.Paragraphs],
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
        var values = request.Template.Values.ToDictionary(
            item => item.Key,
            item => Convert.ToString(item.Value, CultureInfo.InvariantCulture) ?? string.Empty,
            StringComparer.Ordinal);
        var count = 0;
        foreach (var text in documentRoot.Descendants<W.Text>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = text.Text ?? string.Empty;
            var replaced = Placeholder.Replace(original, match => values.GetValueOrDefault(match.Groups["key"].Value, string.Empty));
            if (!string.Equals(replaced, original, StringComparison.Ordinal))
            {
                text.Text = replaced;
                count++;
            }
        }
        mainPart.Document.Save();
        return new ExportRenderResult(count);
    }

    private static string? MetadataValue(IEnumerable<string> lines, string key) =>
        lines.FirstOrDefault(line => line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)) is { } line
            ? line[(key.Length + 1)..].Trim()
            : null;

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
