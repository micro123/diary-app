using System.Globalization;
using ClosedXML.Excel;
using Diary.ScriptHost;

namespace Diary.Export.Xlsx;

internal sealed class XlsxTableExportHandler : IExportHandler
{
    public ExportFormatDescriptor Descriptor { get; } = new(
        "xlsx",
        "Excel 工作簿",
        ".xlsx",
        [".xlsx"],
        [new ExportContentCapabilities(
            ExportContentKind.Table,
            [
                ExportFeature.UnicodeText,
                ExportFeature.TypedValues,
                ExportFeature.NumberFormat,
                ExportFeature.BasicStyle,
                ExportFeature.MergeCells,
                ExportFeature.GeneratedAggregate,
            ])],
        SupportsTemplates: true,
        FormatOptions:
        [
            new ExportFormatOptionDescriptor(
                "sheet_name",
                ExportScalarType.Text,
                DefaultValue: "明细",
                Description: "工作表名称；非法字符会被移除，最长 31 个字符。"),
        ]);

    public async ValueTask<ExportRenderResult> RenderAsync(
        ExportRequest request,
        ExportExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.Content is not ExportTableContent table)
            throw new InvalidOperationException("XLSX 通用导出只支持 table 内容。");
        if (request.FormatOptions is not null
            && request.FormatOptions.Values.Keys.Any(key => !string.Equals(key, "sheet_name", StringComparison.Ordinal)))
            throw new ExportHandlerException("EXPORT_INVALID_REQUEST", "XLSX 格式选项无效。");

        var sheetName = request.FormatOptions?.Values.TryGetValue("sheet_name", out var value) == true
            ? value?.ToString()
            : "明细";
        if (string.IsNullOrWhiteSpace(sheetName))
            sheetName = "明细";

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add(SanitizeSheetName(sheetName));
            var dataStartRow = string.IsNullOrWhiteSpace(table.Title) ? 2 : 3;
            var headerRow = string.IsNullOrWhiteSpace(table.Title) ? 1 : 2;
            var dataEndRow = dataStartRow + table.Rows.Count - 1;

            if (!string.IsNullOrWhiteSpace(table.Title))
            {
                worksheet.Cell(1, 1).Value = table.Title;
                worksheet.Range(1, 1, 1, table.Columns.Count).Merge();
                ApplyTitleStyle(worksheet.Range(1, 1, 1, table.Columns.Count), table.Style);
            }

            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                worksheet.Cell(headerRow, columnIndex + 1).Value = table.Columns[columnIndex].Name;
                ApplyHeaderStyle(worksheet.Cell(headerRow, columnIndex + 1), table.Style);
            }

            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
                {
                    var column = table.Columns[columnIndex];
                    var cell = worksheet.Cell(dataStartRow + rowIndex, columnIndex + 1);
                    var normalized = ExportRequestValidator.NormalizeValue(
                        table.Rows[rowIndex][columnIndex], column.Type, column.Name, rowIndex + 1);
                    SetCellValue(cell, normalized, column.Type);
                    if (!string.IsNullOrWhiteSpace(column.NumberFormat))
                        cell.Style.NumberFormat.Format = column.NumberFormat;
                    else if (column.Type == ExportColumnType.Duration)
                        cell.Style.NumberFormat.Format = "[h]:mm:ss";
                }
            }

            if (table.Aggregates.Count > 0)
            {
                var aggregateRow = Math.Max(dataEndRow + 1, dataStartRow);
                var labelColumnIndex = ExportRequestValidator.GetAggregateLabelColumnIndex(table) + 1;
                worksheet.Cell(aggregateRow, labelColumnIndex).Value = ExportRequestValidator.GetAggregateLabel(table);
                foreach (var aggregate in table.Aggregates)
                {
                    var columnIndex = table.Columns
                        .Select((column, index) => (column, index))
                        .First(item => string.Equals(item.column.Name, aggregate.ColumnName, StringComparison.OrdinalIgnoreCase)).index + 1;
                    var formula = dataEndRow >= dataStartRow
                        ? $"SUM({worksheet.Cell(dataStartRow, columnIndex).Address}:{worksheet.Cell(dataEndRow, columnIndex).Address})"
                        : "0";
                    worksheet.Cell(aggregateRow, columnIndex).FormulaA1 = formula;
                    ApplyTotalStyle(worksheet.Cell(aggregateRow, columnIndex), table.Style);
                }
                ApplyTotalStyle(worksheet.Cell(aggregateRow, labelColumnIndex), table.Style);
            }

            foreach (var merge in table.Merges)
            {
                var rowOffset = dataStartRow - 1;
                worksheet.Range(
                    merge.StartRow + rowOffset,
                    merge.StartColumn,
                    merge.StartRow + merge.RowSpan - 1 + rowOffset,
                    merge.StartColumn + merge.ColumnSpan - 1).Merge();
            }

            worksheet.Columns().AdjustToContents();
            workbook.SaveAs(context.OutputPath);
            return new ExportRenderResult(table.Rows.Count);
        }, cancellationToken);
    }

    private static void SetCellValue(IXLCell cell, object? value, ExportColumnType type)
    {
        if (value is null)
        {
            cell.Clear();
            return;
        }

        switch (type)
        {
            case ExportColumnType.Text:
                cell.SetValue(value.ToString() ?? string.Empty);
                break;
            case ExportColumnType.Integer:
                cell.Value = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                break;
            case ExportColumnType.Decimal:
                cell.Value = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                break;
            case ExportColumnType.Boolean:
                cell.Value = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                break;
            case ExportColumnType.Date:
                cell.Value = ((DateOnly)value).ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "yyyy-mm-dd";
                break;
            case ExportColumnType.Time:
                cell.Value = ((TimeOnly)value).ToTimeSpan();
                cell.Style.DateFormat.Format = "hh:mm:ss";
                break;
            case ExportColumnType.Duration:
                cell.Value = (TimeSpan)value;
                cell.Style.NumberFormat.Format = "[h]:mm:ss";
                break;
            case ExportColumnType.DateTime:
                cell.Value = ((DateTimeOffset)value).LocalDateTime;
                cell.Style.DateFormat.Format = "yyyy-mm-dd hh:mm:ss";
                break;
        }
    }

    private static void ApplyTitleStyle(IXLRange range, ExportTableStyle style)
    {
        range.Style.Font.Bold = true;
        if (style == ExportTableStyle.Compact)
        {
            range.Style.Font.FontSize = 12;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
            return;
        }

        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9EAF7");
        if (style == ExportTableStyle.Report)
            range.Style.Font.FontSize = 16;
    }

    private static void ApplyHeaderStyle(IXLCell cell, ExportTableStyle style)
    {
        cell.Style.Font.Bold = true;
        if (style == ExportTableStyle.Compact)
        {
            cell.Style.Font.FontSize = 10;
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            return;
        }

        cell.Style.Font.FontColor = XLColor.White;
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
        if (style == ExportTableStyle.Report)
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
    }

    private static void ApplyTotalStyle(IXLCell cell, ExportTableStyle style)
    {
        cell.Style.Font.Bold = true;
        if (style == ExportTableStyle.Compact)
        {
            cell.Style.Font.FontSize = 10;
            cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
            return;
        }

        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9EAF7");
        if (style == ExportTableStyle.Report)
            cell.Style.Border.TopBorder = XLBorderStyleValues.Double;
    }

    private static string SanitizeSheetName(string value)
    {
        var filtered = new string(value.Where(character => !"[]:*?/\\".Contains(character)).ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? "明细" : filtered[..Math.Min(31, filtered.Length)];
    }
}
