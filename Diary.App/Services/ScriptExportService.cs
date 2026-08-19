using System.Collections.Concurrent;
using System.Globalization;
using ClosedXML.Excel;
using Diary.ScriptHost;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Microsoft.Extensions.Logging;

namespace Diary.App.Services;

public sealed class ScriptExportService(
    ILogger<ScriptExportService> logger) : IExportApi
{
    private readonly ConcurrentDictionary<string, DirectorySelectionEntry> _directories = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExportFileEntry> _files = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime = TimeSpan.FromMinutes(10);

    public void RegisterDirectory(string selectionId, string path, ScriptHostCallContext context)
    {
        _directories[selectionId] = new DirectorySelectionEntry(
            selectionId,
            context.ExecutionId,
            context.WorkerId,
            path,
            DateTimeOffset.UtcNow.Add(_lifetime));
    }

    public bool TryGetDirectory(string selectionId, ScriptHostCallContext context, out string path)
    {
        path = string.Empty;
        if (!_directories.TryGetValue(selectionId, out var entry)
            || entry.ExpiresAt <= DateTimeOffset.UtcNow
            || !string.Equals(entry.ExecutionId, context.ExecutionId, StringComparison.Ordinal)
            || !string.Equals(entry.WorkerId, context.WorkerId, StringComparison.Ordinal))
            return false;
        path = entry.Path;
        return true;
    }

    public bool TryGetFile(string fileId, ScriptHostCallContext context, out string path, out string fileName)
    {
        path = string.Empty;
        fileName = string.Empty;
        if (!_files.TryGetValue(fileId, out var entry)
            || entry.ExpiresAt <= DateTimeOffset.UtcNow
            || !string.Equals(entry.ExecutionId, context.ExecutionId, StringComparison.Ordinal)
            || !string.Equals(entry.WorkerId, context.WorkerId, StringComparison.Ordinal))
            return false;
        path = entry.Path;
        fileName = entry.FileName;
        return true;
    }

    public ValueTask<IReadOnlyList<ExportFormatDescriptor>> ListFormatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<ExportFormatDescriptor>>(
        [
            new ExportFormatDescriptor(
                "xlsx",
                "Excel 工作簿",
                ".xlsx",
                [".xlsx"],
                [new ExportContentCapabilities(
                    ExportContentKind.Table,
                    [
                        ExportFeature.UnicodeText,
                        ExportFeature.TypedValues,
                        ExportFeature.BasicStyle,
                        ExportFeature.BackgroundColor,
                        ExportFeature.MergeCells,
                        ExportFeature.GeneratedAggregate,
                    ])]),
        ]);
    }

    public async ValueTask<ExportResult> ExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var descriptor = (await ListFormatsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.FormatId, request.FormatId, StringComparison.Ordinal));
        if (descriptor is null)
            return Failure(request, "EXPORT_INVALID_REQUEST", "不支持的导出格式。", ScriptErrorCategory.Validation);

        var validation = ExportRequestValidator.Validate(request, descriptor);
        if (validation is not null)
            return request.Content is null
                ? new(false, request.FormatId, ExportContentKind.Table, null, null, null, validation)
                : new(false, request.FormatId, request.Content.Kind, null, null, null, validation);

        if (request.Content is not ExportTableContent table)
            return Failure(request, "EXPORT_INVALID_REQUEST", "当前导出处理器只支持表格内容。", ScriptErrorCategory.Validation);

        if (request.FormatOptions is not null
            && (!string.Equals(request.FormatOptions.FormatId, request.FormatId, StringComparison.Ordinal)
                || request.FormatOptions.Values.Keys.Any(key => !string.Equals(key, "sheetName", StringComparison.Ordinal))))
            return Failure(request, "EXPORT_INVALID_REQUEST", "XLSX 格式选项无效。", ScriptErrorCategory.Validation);

        if (!TryGetDirectory(request.DirectorySelectionId, new ScriptHostCallContext(
                "", "", "", ScriptEntryKind.Application, ScriptExecutionSource.Unknown), out _))
        {
            // 实际执行路径使用带上下文的 ExportAsync overload；这里保留接口兼容并阻止无上下文调用。
            return Failure(request, "DIRECTORY_SELECTION_INVALID", "目录选择令牌无效。", ScriptErrorCategory.Permission);
        }

        return Failure(request, "DIRECTORY_SELECTION_INVALID", "导出请求缺少执行上下文。", ScriptErrorCategory.Permission);
    }

    public async ValueTask<ExportResult> ExportAsync(
        ExportRequest request,
        ScriptHostCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (!ScriptHostCallScope.AllowsInteractive(context))
            return Failure(request, "HOSTCALL_SCOPE_NOT_SUPPORTED", "当前脚本执行入口不允许导出。", ScriptErrorCategory.Permission);

        var descriptor = (await ListFormatsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.FormatId, request.FormatId, StringComparison.Ordinal));
        if (descriptor is null)
            return Failure(request, "EXPORT_INVALID_REQUEST", "不支持的导出格式。", ScriptErrorCategory.Validation);

        var validation = ExportRequestValidator.Validate(request, descriptor);
        if (validation is not null)
            return new(false, request.FormatId, request.Content.Kind, null, null, null, validation);
        if (request.Content is not ExportTableContent table)
            return Failure(request, "EXPORT_INVALID_REQUEST", "当前导出处理器只支持表格内容。", ScriptErrorCategory.Validation);
        if (!TryGetDirectory(request.DirectorySelectionId, context, out var directory))
            return Failure(request, "DIRECTORY_SELECTION_INVALID", "目录选择令牌无效或已过期。", ScriptErrorCategory.Permission);

        var sheetName = request.FormatOptions?.Values.TryGetValue("sheetName", out var value) == true
            ? value?.ToString()
            : "明细";
        if (string.IsNullOrWhiteSpace(sheetName))
            sheetName = "明细";

        var finalName = EnsureExtension(request.FileName, descriptor.DefaultExtension);
        var outputPath = GetUniquePath(directory, finalName);
        try
        {
            Directory.CreateDirectory(directory);
            await Task.Run(() => WriteXlsx(outputPath, sheetName, table, cancellationToken), cancellationToken);
            var fileId = Guid.NewGuid().ToString("N");
            _files[fileId] = new ExportFileEntry(
                fileId,
                context.ExecutionId,
                context.WorkerId,
                outputPath,
                Path.GetFileName(outputPath),
                DateTimeOffset.UtcNow.Add(_lifetime));
            return new(true, request.FormatId, table.Kind, fileId, Path.GetFileName(outputPath), table.Rows.Count, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            throw;
        }
        catch (Exception exception)
        {
            TryDelete(outputPath);
            logger.LogWarning(exception, "脚本导出失败：{Path}", outputPath);
            return Failure(request, "EXPORT_FAILED", "导出文件生成或保存失败。", ScriptErrorCategory.Host, true);
        }
    }

    private static void WriteXlsx(string path, string sheetName, ExportTableContent table, CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(SanitizeSheetName(sheetName));
        var dataStartRow = string.IsNullOrWhiteSpace(table.Title) ? 2 : 3;
        var headerRow = string.IsNullOrWhiteSpace(table.Title) ? 1 : 2;
        var dataEndRow = dataStartRow + table.Rows.Count - 1;

        if (!string.IsNullOrWhiteSpace(table.Title))
        {
            worksheet.Cell(1, 1).Value = table.Title;
            worksheet.Range(1, 1, 1, table.Columns.Count).Merge();
            ApplyTitleStyle(worksheet.Range(1, 1, 1, table.Columns.Count));
        }

        for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
        {
            worksheet.Cell(headerRow, columnIndex + 1).Value = table.Columns[columnIndex].Name;
            ApplyHeaderStyle(worksheet.Cell(headerRow, columnIndex + 1));
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
            worksheet.Cell(aggregateRow, 1).Value = "合计";
            foreach (var aggregate in table.Aggregates)
            {
                var columnIndex = table.Columns
                    .Select((column, index) => (column, index))
                    .First(item => string.Equals(item.column.Name, aggregate.ColumnName, StringComparison.OrdinalIgnoreCase)).index + 1;
                var formula = dataEndRow >= dataStartRow
                    ? $"SUM({worksheet.Cell(dataStartRow, columnIndex).Address}:{worksheet.Cell(dataEndRow, columnIndex).Address})"
                    : "0";
                worksheet.Cell(aggregateRow, columnIndex).FormulaA1 = formula;
                ApplyTotalStyle(worksheet.Cell(aggregateRow, columnIndex));
            }
            ApplyTotalStyle(worksheet.Cell(aggregateRow, 1));
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
        workbook.SaveAs(path);
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

    private static void ApplyTitleStyle(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9EAF7");
    }

    private static void ApplyHeaderStyle(IXLCell cell)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = XLColor.White;
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
    }

    private static void ApplyTotalStyle(IXLCell cell)
    {
        cell.Style.Font.Bold = true;
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#D9EAF7");
    }

    private static string EnsureExtension(string fileName, string extension) =>
        Path.HasExtension(fileName) ? fileName : fileName + extension;

    private static string GetUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
            return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; ; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static string SanitizeSheetName(string value)
    {
        var filtered = new string(value.Where(character => !"[]:*?/\\".Contains(character)).ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? "明细" : filtered[..Math.Min(31, filtered.Length)];
    }

    private static ExportResult Failure(
        ExportRequest request,
        string code,
        string message,
        ScriptErrorCategory category,
        bool retryable = false) =>
        new(false, request.FormatId, request.Content.Kind, null, null, null,
            new ScriptApiError(code, message, category, retryable));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 失败清理不覆盖原始导出错误。
        }
    }

    private sealed record DirectorySelectionEntry(
        string SelectionId,
        string ExecutionId,
        string WorkerId,
        string Path,
        DateTimeOffset ExpiresAt);

    private sealed record ExportFileEntry(
        string FileId,
        string ExecutionId,
        string WorkerId,
        string Path,
        string FileName,
        DateTimeOffset ExpiresAt);
}
