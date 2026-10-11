using System.Collections.Concurrent;
using System.Diagnostics;
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
    string outputDirectory,
    IAgentCapabilityApi? capabilities = null) : IAgentReportExportApi
{
    private const int MaxItems = 10_000;
    private const int MaxIdempotencyKeyLength = 200;
    private const string DirectorySelectionId = "ai-agent-report-directory";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ScriptHostCallContext ExportContext = new(
        "ai-agent-report", "in-process", "diary.ai-agent", ScriptEntryKind.Application, ScriptExecutionSource.Manual);
    private readonly ConcurrentDictionary<string, AgentReportExportResult> _completed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);
    private readonly object _artifactGate = new();
    private readonly string _artifactPath = Path.Combine(outputDirectory, ".report-artifacts.json");
    private readonly List<ArtifactEntry> _artifacts = LoadArtifacts(
        Path.Combine(outputDirectory, ".report-artifacts.json"));

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

        var query = await ResolveItemsAsync(normalizedCommand, startDate, endDate, cancellationToken);
        if (!query.Succeeded)
            return FailedPreview(query.ErrorCode!, query.ErrorMessage!);
        startDate = query.StartDate!;
        endDate = query.EndDate!;
        var request = await BuildRequestAsync(normalizedCommand, startDate, endDate, query.Items!, validateOnly: true, cancellationToken);
        if (request is null)
            return FailedPreview("export_template_not_found", "指定的数据模板不存在或格式不匹配。");
        var validation = ExportRequestValidator.Validate(request, format);
        if (validation is not null)
            return FailedPreview(validation.Code, validation.Message);
        var version = CreatePreviewVersion(normalizedCommand, query.Items!, format, request.Template);
        return new(
            true,
            normalizedCommand with { PreviewVersion = version, StartDate = startDate, EndDate = endDate },
            startDate,
            endDate,
            query.Items!.Count,
            query.Items.Sum(item => item.Hours),
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
            if (!FixedEquals(command.PreviewVersion, preview.PreviewVersion))
                return FailedResult("preview_stale", "报告导出预览已过期，请重新预览并确认。", preview.PreviewVersion);

            var query = await ResolveItemsAsync(preview.Command, preview.StartDate!, preview.EndDate!, cancellationToken);
            if (!query.Succeeded)
                return FailedResult(query.ErrorCode!, query.ErrorMessage!, preview.PreviewVersion);
            var format = (await exports.ListFormatsAsync(cancellationToken))
                .First(item => string.Equals(item.FormatId, preview.Command.FormatId, StringComparison.Ordinal));
            var request = await BuildRequestAsync(preview.Command, preview.StartDate!, preview.EndDate!, query.Items!, false, cancellationToken);
            if (request is null)
                return FailedResult("export_template_not_found", "指定的数据模板不存在或格式不匹配。", preview.PreviewVersion);
            var executionVersion = CreatePreviewVersion(preview.Command with { PreviewVersion = null }, query.Items!, format, request.Template);
            if (!FixedEquals(command.PreviewVersion, executionVersion))
                return FailedResult("preview_stale", "报告数据在确认后发生变化，请重新预览并确认。", executionVersion);

            Directory.CreateDirectory(outputDirectory);
            exports.RegisterDirectory(DirectorySelectionId, outputDirectory, ExportContext);
            var exportResult = await exports.ExportAsync(request, ExportContext, cancellationToken);
            if (!exportResult.Succeeded || string.IsNullOrWhiteSpace(exportResult.FileName))
                return FailedResult(exportResult.Error?.Code ?? "export_failed", exportResult.Error?.Message ?? "报告导出失败。", preview.PreviewVersion);
            var artifact = RegisterArtifact(exportResult.FileName, preview.Command.FormatId);
            var result = new AgentReportExportResult(
                true, exportResult.FileName, exportResult.ItemCount, false, preview.PreviewVersion, ArtifactId: artifact.ArtifactId);
            _completed[key] = result;
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public IReadOnlyList<AgentReportArtifact> ListArtifacts(int limit = 20)
    {
        lock (_artifactGate)
        {
            return _artifacts.OrderByDescending(item => item.CreatedAt)
                .Take(Math.Clamp(limit, 1, 100))
                .Select(ToArtifact)
                .ToArray();
        }
    }

    public AgentReportExportResult LocateArtifact(string artifactId)
    {
        ArtifactEntry? artifact;
        lock (_artifactGate)
            artifact = _artifacts.FirstOrDefault(item => item.ArtifactId == artifactId);
        if (artifact is null)
            return FailedResult("artifact_not_found", "报告记录不存在。");
        var path = ResolveArtifactPath(artifact.FileName);
        if (path is null || !File.Exists(path))
            return FailedResult("artifact_missing", "报告文件已不存在。");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = outputDirectory, UseShellExecute = true });
            }
            return new(true, artifact.FileName, null, false, null, ArtifactId: artifact.ArtifactId);
        }
        catch
        {
            return FailedResult("artifact_locate_failed", "无法在文件管理器中定位报告。");
        }
    }

    private async ValueTask<QueryResult> ResolveItemsAsync(
        AgentReportExportCommand command,
        string startDate,
        string endDate,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(command.SavedQueryId))
        {
            if (capabilities is null)
                return new(false, null, null, null, "saved_query_not_supported", "当前宿主不支持保存查询报告。");
            var saved = await capabilities.RunSavedQueryAsync(command.SavedQueryId, MaxItems, cancellationToken);
            if (!saved.Succeeded)
                return new(false, null, null, null, saved.ErrorCode, saved.ErrorMessage);
            var items = saved.Items;
            var resolvedStart = saved.Query?.StartDate ?? items.Select(item => item.Date).DefaultIfEmpty(startDate).Min();
            var resolvedEnd = saved.Query?.EndDate ?? items.Select(item => item.Date).DefaultIfEmpty(endDate).Max();
            return new(true, items, resolvedStart, resolvedEnd, null, null);
        }
        try
        {
            var items = new List<ScriptWorkItem>();
            await foreach (var item in workItems.StreamAsync(new ScriptWorkItemQuery
            {
                StartDate = startDate,
                EndDate = endDate,
            }, cancellationToken: cancellationToken))
            {
                if (items.Count >= MaxItems)
                    return new(false, null, null, null, "report_too_large", $"报告事项不能超过 {MaxItems} 条。");
                items.Add(item);
            }
            return new(true, items, startDate, endDate, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(false, null, null, null, "query_failed", "查询报告事项失败。"); }
    }

    private async ValueTask<ExportRequest?> BuildRequestAsync(
        AgentReportExportCommand command,
        string startDate,
        string endDate,
        IReadOnlyList<ScriptWorkItem> items,
        bool validateOnly,
        CancellationToken cancellationToken)
    {
        var main = BuildMainTable(command, startDate, endDate, items);
        ExportTemplateSource? template = null;
        if (!string.IsNullOrWhiteSpace(command.TemplateId))
        {
            var descriptor = (await exports.ListTemplatesAsync(command.FormatId, cancellationToken))
                .Where(item => string.Equals(item.TemplateId, command.TemplateId, StringComparison.Ordinal))
                .OrderByDescending(item => item.TemplateVersion, StringComparer.Ordinal)
                .FirstOrDefault();
            if (descriptor is null)
                return null;
            template = new ExportTemplateSource
            {
                TemplateId = descriptor.TemplateId,
                TemplateVersion = descriptor.TemplateVersion,
                Values = new Dictionary<string, object?>
                {
                    ["title"] = main.Title,
                    ["start_date"] = startDate,
                    ["end_date"] = endDate,
                    ["item_count"] = items.Count,
                    ["total_hours"] = items.Sum(item => item.Hours),
                },
                Tables = BuildTemplateTables(command, main, items),
            };
        }
        return new ExportRequest
        {
            FormatId = command.FormatId,
            DirectorySelectionId = DirectorySelectionId,
            FileName = command.FileName,
            Content = template is null ? main : null,
            Template = template,
            ValidateOnly = validateOnly,
        };
    }

    private static ExportTableContent BuildMainTable(
        AgentReportExportCommand command,
        string startDate,
        string endDate,
        IReadOnlyList<ScriptWorkItem> items)
    {
        var rows = items.Select(item => (IReadOnlyList<object?>)
        [
            item.Date,
            item.Comment,
            item.Hours,
            item.Priority,
            string.Join("、", item.Tags.Select(tag => tag.Name)),
        ]).ToList();
        if (command.IncludeSummary)
            rows.Add([endDate, "合计", items.Sum(item => item.Hours), null, $"{items.Count} 条事项"]);
        if (command.IncludeDailyHours)
        {
            foreach (var day in items.GroupBy(item => item.Date).OrderBy(group => group.Key))
                rows.Add([day.Key, "每日工时", day.Sum(item => item.Hours), null, null]);
        }
        if (command.IncludeTagStatistics)
        {
            foreach (var tag in items.SelectMany(item => item.Tags.Select(value => (value.Name, item.Hours)))
                         .GroupBy(item => item.Name).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                rows.Add([endDate, $"标签统计：{tag.Key}", tag.Sum(item => item.Hours), null, $"{tag.Count()} 次"]);
        }
        return new ExportTableContent
        {
            Title = BuildTitle(command.Period, startDate, endDate),
            Columns =
            [
                new ExportColumn("日期", ExportColumnType.Date),
                new ExportColumn("事项"),
                new ExportColumn("工时", ExportColumnType.Decimal),
                new ExportColumn("优先级", ExportColumnType.Integer),
                new ExportColumn("标签"),
            ],
            Rows = rows,
            Style = ExportTableStyle.Default,
        };
    }

    private static IReadOnlyDictionary<string, ExportTableContent> BuildTemplateTables(
        AgentReportExportCommand command,
        ExportTableContent main,
        IReadOnlyList<ScriptWorkItem> items)
    {
        var tables = new Dictionary<string, ExportTableContent>(StringComparer.Ordinal)
        {
            ["items"] = main,
        };
        if (command.IncludeDailyHours)
        {
            tables["daily_hours"] = new ExportTableContent
            {
                Title = "逐日工时",
                Columns = [new ExportColumn("日期", ExportColumnType.Date), new ExportColumn("工时", ExportColumnType.Decimal)],
                Rows = items.GroupBy(item => item.Date).OrderBy(group => group.Key)
                    .Select(group => (IReadOnlyList<object?>)[group.Key, group.Sum(item => item.Hours)]).ToArray(),
            };
        }
        if (command.IncludeTagStatistics)
        {
            tables["tag_statistics"] = new ExportTableContent
            {
                Title = "标签统计",
                Columns = [new ExportColumn("标签"), new ExportColumn("次数", ExportColumnType.Integer), new ExportColumn("工时", ExportColumnType.Decimal)],
                Rows = items.SelectMany(item => item.Tags.Select(tag => (tag.Name, item.Hours))).GroupBy(item => item.Name)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => (IReadOnlyList<object?>)[group.Key, group.Count(), group.Sum(item => item.Hours)]).ToArray(),
            };
        }
        return tables;
    }

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
        var formatId = command.FormatId?.Trim() ?? string.Empty;
        var fileName = command.FileName?.Trim() ?? string.Empty;
        var idempotencyKey = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (formatId.Length is 0 or > 100)
            return Fail("导出格式 ID 不能为空且不能超过 100 个字符。", out error);
        if (fileName.Length is 0 or > ExportRequestValidator.MaxFileNameLength)
            return Fail("导出文件名为空或过长。", out error);
        if (idempotencyKey.Length is 0 or > MaxIdempotencyKeyLength)
            return Fail($"幂等键不能为空且不能超过 {MaxIdempotencyKeyLength} 个字符。", out error);

        DateOnly start;
        DateOnly end;
        if (command.Period == AgentReportPeriod.Custom)
        {
            if (!TryDate(command.StartDate, out start) || !TryDate(command.EndDate, out end) || start > end)
                return Fail("自定义报告必须提供有效的 startDate 和 endDate，且开始日期不能晚于结束日期。", out error);
        }
        else if (!string.IsNullOrWhiteSpace(command.SavedQueryId) && string.IsNullOrWhiteSpace(command.Date))
        {
            start = end = DateOnly.FromDateTime(DateTime.Today);
        }
        else
        {
            if (!TryDate(command.Date, out var date))
                return Fail("报告日期必须是 yyyy-MM-dd 格式。", out error);
            (start, end) = command.Period switch
            {
                AgentReportPeriod.Day => (date, date),
                AgentReportPeriod.Week => (date.AddDays(-(((int)date.DayOfWeek + 6) % 7)), date.AddDays(6 - (((int)date.DayOfWeek + 6) % 7))),
                AgentReportPeriod.Month => (new DateOnly(date.Year, date.Month, 1), new DateOnly(date.Year, date.Month, 1).AddMonths(1).AddDays(-1)),
                AgentReportPeriod.Quarter => QuarterRange(date),
                AgentReportPeriod.Year => (new DateOnly(date.Year, 1, 1), new DateOnly(date.Year, 12, 31)),
                _ => (date, date),
            };
        }
        startDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        endDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        normalized = command with
        {
            Date = string.IsNullOrWhiteSpace(command.Date) ? startDate : command.Date.Trim(),
            StartDate = startDate,
            EndDate = endDate,
            SavedQueryId = string.IsNullOrWhiteSpace(command.SavedQueryId) ? null : command.SavedQueryId.Trim(),
            TemplateId = string.IsNullOrWhiteSpace(command.TemplateId) ? null : command.TemplateId.Trim(),
            FormatId = formatId,
            FileName = fileName,
            IdempotencyKey = idempotencyKey,
            PreviewVersion = null,
        };
        return true;
    }

    private ArtifactEntry RegisterArtifact(string fileName, string formatId)
    {
        var path = ResolveArtifactPath(fileName)!;
        var entry = new ArtifactEntry(Guid.NewGuid().ToString("N"), fileName, formatId, DateTimeOffset.Now);
        lock (_artifactGate)
        {
            _artifacts.Add(entry);
            while (_artifacts.Count > 500)
                _artifacts.RemoveAt(0);
            SaveArtifacts();
        }
        return entry;
    }

    private AgentReportArtifact ToArtifact(ArtifactEntry entry)
    {
        var path = ResolveArtifactPath(entry.FileName);
        var exists = path is not null && File.Exists(path);
        return new(entry.ArtifactId, entry.FileName, entry.FormatId, entry.CreatedAt, exists ? new FileInfo(path!).Length : 0, exists);
    }

    private string? ResolveArtifactPath(string fileName)
    {
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            return null;
        var root = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(outputDirectory, fileName));
        return path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? path
            : null;
    }

    private void SaveArtifacts()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_artifactPath)!);
        var temporary = _artifactPath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new ArtifactEnvelope(1, _artifacts), JsonOptions), Encoding.UTF8);
        File.Move(temporary, _artifactPath, true);
    }

    private static List<ArtifactEntry> LoadArtifacts(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out _))
            {
                var envelope = JsonSerializer.Deserialize<ArtifactEnvelope>(json, JsonOptions);
                return envelope?.SchemaVersion == 1 ? envelope.Items : [];
            }
            return JsonSerializer.Deserialize<List<ArtifactEntry>>(json, JsonOptions) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private static string CreatePreviewVersion(
        AgentReportExportCommand command,
        IReadOnlyList<ScriptWorkItem> items,
        ExportFormatDescriptor format,
        ExportTemplateSource? template) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Command = command with { PreviewVersion = null },
            Format = new { format.FormatId, format.DefaultExtension, format.AllowedExtensions },
            Template = template is null ? null : new { template.TemplateId, template.TemplateVersion },
            Items = items.Select(item => new { item.Id, item.Date, item.Comment, item.Hours, item.Priority, Tags = item.Tags.Select(tag => new { tag.Id, tag.Name }) }),
        })));

    private static string BuildTitle(AgentReportPeriod period, string startDate, string endDate) => period switch
    {
        AgentReportPeriod.Day => $"工作日报 {startDate}",
        AgentReportPeriod.Week => $"工作周报 {startDate} 至 {endDate}",
        AgentReportPeriod.Month => $"工作月报 {startDate[..7]}",
        AgentReportPeriod.Quarter => $"工作季报 {startDate} 至 {endDate}",
        AgentReportPeriod.Year => $"工作年报 {startDate[..4]}",
        _ => $"工作报告 {startDate} 至 {endDate}",
    };

    private static (DateOnly Start, DateOnly End) QuarterRange(DateOnly date)
    {
        var month = ((date.Month - 1) / 3) * 3 + 1;
        var start = new DateOnly(date.Year, month, 1);
        return (start, start.AddMonths(3).AddDays(-1));
    }

    private static bool TryDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool FixedEquals(string? left, string right) => !string.IsNullOrWhiteSpace(left)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static AgentReportExportPreview FailedPreview(string code, string message) =>
        new(false, null, null, null, 0, 0, null, code, message);

    private static AgentReportExportResult FailedResult(string code, string message, string? previewVersion = null) =>
        new(false, null, null, false, previewVersion, code, message);

    private static bool Fail(string message, out string error) { error = message; return false; }

    private sealed record QueryResult(
        bool Succeeded,
        IReadOnlyList<ScriptWorkItem>? Items,
        string? StartDate,
        string? EndDate,
        string? ErrorCode,
        string? ErrorMessage);

    private sealed record ArtifactEntry(string ArtifactId, string FileName, string FormatId, DateTimeOffset CreatedAt);

    private sealed record ArtifactEnvelope(int SchemaVersion, List<ArtifactEntry> Items);
}
