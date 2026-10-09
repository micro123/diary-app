using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.App.Services;

public sealed class AgentReportExportApi(
    IWorkItemQueryScriptApi workItems,
    ScriptExportService exports,
    string outputDirectory) : IAgentReportExportApi
{
    private const int MaxItems = 10_000;
    private const int MaxIdempotencyKeyLength = 200;
    private const string DirectorySelectionId = "ai-agent-report-directory";
    private static readonly ScriptHostCallContext ExportContext = new(
        "ai-agent-report",
        "in-process",
        "diary.ai-agent",
        ScriptEntryKind.Application,
        ScriptExecutionSource.Manual);
    private readonly ConcurrentDictionary<string, AgentReportExportResult> _completed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);

    public async ValueTask<AgentReportExportPreview> PreviewAsync(
        AgentReportExportCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(command, out var normalized, out var startDate, out var endDate, out var error))
            return FailedPreview("invalid_command", error);
        var normalizedCommand = normalized!;
        var formats = await exports.ListFormatsAsync(cancellationToken);
        var format = formats.FirstOrDefault(item => string.Equals(item.FormatId, normalizedCommand.FormatId, StringComparison.Ordinal));
        if (format is null)
            return FailedPreview("export_format_not_found", $"导出格式 {normalizedCommand.FormatId} 不存在。");
        if (!format.ContentCapabilities.Any(item => item.ContentKind == ExportContentKind.Table))
            return FailedPreview("export_format_unsupported", $"导出格式 {format.FormatId} 不支持表格报告。");

        var queryResult = await QueryItemsAsync(startDate, endDate, cancellationToken);
        if (!queryResult.Succeeded)
            return FailedPreview(queryResult.ErrorCode!, queryResult.ErrorMessage!);
        var content = BuildContent(normalizedCommand.Period, startDate, endDate, queryResult.Items!);
        var request = new ExportRequest
        {
            FormatId = normalizedCommand.FormatId,
            DirectorySelectionId = DirectorySelectionId,
            FileName = normalizedCommand.FileName,
            Content = content,
            ValidateOnly = true,
        };
        var validation = ExportRequestValidator.Validate(request, format);
        if (validation is not null)
            return FailedPreview(validation.Code, validation.Message);

        var version = CreatePreviewVersion(normalizedCommand, queryResult.Items!, format);
        return new AgentReportExportPreview(
            true,
            normalizedCommand with { PreviewVersion = version },
            startDate,
            endDate,
            queryResult.Items!.Count,
            queryResult.Items.Sum(item => item.Hours),
            version);
    }

    public async ValueTask<AgentReportExportResult> ExportAsync(
        AgentReportExportCommand command,
        CancellationToken cancellationToken = default)
    {
        var key = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (key.Length is 0 or > MaxIdempotencyKeyLength)
            return FailedResult("idempotency_key_required", "报告导出必须提供有效幂等键。");
        var gate = _keyLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (_completed.TryGetValue(key, out var completed))
                return completed with { Duplicate = true };
            var preview = await PreviewAsync(command with { PreviewVersion = null }, cancellationToken);
            if (!preview.Succeeded || preview.Command is null || preview.PreviewVersion is null)
                return FailedResult(preview.ErrorCode ?? "preview_failed", preview.ErrorMessage ?? "报告导出预览失败。");
            if (string.IsNullOrWhiteSpace(command.PreviewVersion)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(command.PreviewVersion),
                    Encoding.UTF8.GetBytes(preview.PreviewVersion)))
            {
                return FailedResult(
                    "preview_stale",
                    "报告导出预览已过期，请重新预览并确认。",
                    preview.PreviewVersion);
            }

            var queryResult = await QueryItemsAsync(preview.StartDate!, preview.EndDate!, cancellationToken);
            if (!queryResult.Succeeded)
                return FailedResult(queryResult.ErrorCode!, queryResult.ErrorMessage!, preview.PreviewVersion);
            var format = (await exports.ListFormatsAsync(cancellationToken))
                .First(item => string.Equals(item.FormatId, preview.Command.FormatId, StringComparison.Ordinal));
            var executionVersion = CreatePreviewVersion(
                preview.Command with { PreviewVersion = null },
                queryResult.Items!,
                format);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(command.PreviewVersion!),
                    Encoding.UTF8.GetBytes(executionVersion)))
            {
                return FailedResult(
                    "preview_stale",
                    "报告数据在确认后发生变化，请重新预览并确认。",
                    executionVersion);
            }
            Directory.CreateDirectory(outputDirectory);
            exports.RegisterDirectory(DirectorySelectionId, outputDirectory, ExportContext);
            var exportResult = await exports.ExportAsync(new ExportRequest
            {
                FormatId = preview.Command.FormatId,
                DirectorySelectionId = DirectorySelectionId,
                FileName = preview.Command.FileName,
                Content = BuildContent(
                    preview.Command.Period,
                    preview.StartDate!,
                    preview.EndDate!,
                    queryResult.Items!),
            }, ExportContext, cancellationToken);
            if (!exportResult.Succeeded)
                return FailedResult(
                    exportResult.Error?.Code ?? "export_failed",
                    exportResult.Error?.Message ?? "报告导出失败。",
                    preview.PreviewVersion);
            var result = new AgentReportExportResult(
                true,
                exportResult.FileName,
                exportResult.ItemCount,
                false,
                preview.PreviewVersion);
            _completed[key] = result;
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<QueryResult> QueryItemsAsync(
        string startDate,
        string endDate,
        CancellationToken cancellationToken)
    {
        var items = new List<ScriptWorkItem>();
        try
        {
            await foreach (var item in workItems.StreamAsync(
                               new ScriptWorkItemQuery { StartDate = startDate, EndDate = endDate },
                               cancellationToken: cancellationToken))
            {
                if (items.Count >= MaxItems)
                    return new QueryResult(false, null, "report_too_large", $"报告事项数量超过 {MaxItems} 条。");
                items.Add(item);
            }
            return new QueryResult(true, items, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new QueryResult(false, null, "query_failed", "查询报告事项失败。");
        }
    }

    private static ExportTableContent BuildContent(
        AgentReportPeriod period,
        string startDate,
        string endDate,
        IReadOnlyList<ScriptWorkItem> items) => new()
        {
            Title = period == AgentReportPeriod.Day
            ? $"工作日报 {startDate}"
            : $"工作周报 {startDate} 至 {endDate}",
            Columns =
        [
            new ExportColumn("日期", ExportColumnType.Date),
            new ExportColumn("事项"),
            new ExportColumn("工时", ExportColumnType.Decimal),
            new ExportColumn("优先级", ExportColumnType.Integer),
            new ExportColumn("标签"),
        ],
            Rows = items.Select(item => (IReadOnlyList<object?>)
            [
                item.Date,
            item.Comment,
            item.Hours,
            item.Priority,
            string.Join("、", item.Tags.Select(tag => tag.Name)),
        ]).ToArray(),
            Style = ExportTableStyle.Default,
        };

    private static bool TryNormalize(
        AgentReportExportCommand command,
        out AgentReportExportCommand? normalized,
        out string startDate,
        out string endDate,
        out string error)
    {
        normalized = null;
        startDate = endDate = string.Empty;
        error = string.Empty;
        if (!Enum.IsDefined(command.Period))
            return Fail("报告周期无效。", out error);
        if (!DateOnly.TryParseExact(command.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return Fail("报告日期必须是 yyyy-MM-dd 格式。", out error);
        var formatId = command.FormatId?.Trim() ?? string.Empty;
        if (formatId.Length is 0 or > 100)
            return Fail("导出格式 ID 不能为空且不能超过 100 个字符。", out error);
        var fileName = command.FileName?.Trim() ?? string.Empty;
        if (fileName.Length is 0 or > ExportRequestValidator.MaxFileNameLength)
            return Fail("导出文件名为空或过长。", out error);
        var idempotencyKey = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (idempotencyKey.Length is 0 or > MaxIdempotencyKeyLength)
            return Fail($"幂等键不能为空且不能超过 {MaxIdempotencyKeyLength} 个字符。", out error);
        var start = command.Period == AgentReportPeriod.Day
            ? date
            : date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        var end = command.Period == AgentReportPeriod.Day ? start : start.AddDays(6);
        startDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        endDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        normalized = command with
        {
            Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            FormatId = formatId,
            FileName = fileName,
            IdempotencyKey = idempotencyKey,
            PreviewVersion = null,
        };
        return true;
    }

    private static string CreatePreviewVersion(
        AgentReportExportCommand command,
        IReadOnlyList<ScriptWorkItem> items,
        ExportFormatDescriptor format)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Command = command with { PreviewVersion = null },
            Format = new { format.FormatId, format.DefaultExtension, format.AllowedExtensions },
            Items = items.Select(item => new
            {
                item.Id,
                item.Date,
                item.Comment,
                item.Hours,
                item.Priority,
                Tags = item.Tags.Select(tag => new { tag.Id, tag.Name }),
            }),
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static AgentReportExportPreview FailedPreview(string code, string message) =>
        new(false, null, null, null, 0, 0, null, code, message);

    private static AgentReportExportResult FailedResult(
        string code,
        string message,
        string? previewVersion = null) =>
        new(false, null, null, false, previewVersion, code, message);

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private sealed record QueryResult(
        bool Succeeded,
        IReadOnlyList<ScriptWorkItem>? Items,
        string? ErrorCode,
        string? ErrorMessage);
}
