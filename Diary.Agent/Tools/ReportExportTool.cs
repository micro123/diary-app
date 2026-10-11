using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.ScriptHost;

namespace Diary.Agent.Tools;

public sealed class ReportExportTool(
    IAgentReportExportApi exportApi,
    IAgentConfirmationService confirmationService) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "period":{"type":"string","enum":["day","week","month","quarter","year","custom"]},
            "date":{"type":["string","null"],"description":"周期中的任意日期，yyyy-MM-dd"},
            "startDate":{"type":["string","null"],"description":"自定义开始日期，yyyy-MM-dd"},
            "endDate":{"type":["string","null"],"description":"自定义结束日期，yyyy-MM-dd"},
            "savedQueryId":{"type":["string","null"]},
            "templateId":{"type":["string","null"]},
            "includeSummary":{"type":"boolean"},
            "includeTagStatistics":{"type":"boolean"},
            "includeDailyHours":{"type":"boolean"},
            "formatId":{"type":"string"},
            "fileName":{"type":"string"},
            "idempotencyKey":{"type":"string"}
          },
          "required":["period","formatId","fileName","idempotencyKey"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.reports.export",
        "diary_export_report",
        "导出日报或周报",
        "预览指定日期或所在周的事项报告，并且仅在用户逐次确认后写入应用管理的 AI 导出目录。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.Write,
        "diary.ai-agent",
        "ReportExport");

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ReportExportArguments? input;
        try
        {
            input = arguments.Deserialize<ReportExportArguments>(JsonOptions);
        }
        catch (JsonException)
        {
            return AgentToolResult.Failure("invalid_arguments", "报告导出参数格式无效。");
        }
        if (input is null
            || !TryParsePeriod(input.Period, out var period)
            || string.IsNullOrWhiteSpace(input.FormatId)
            || string.IsNullOrWhiteSpace(input.FileName)
            || string.IsNullOrWhiteSpace(input.IdempotencyKey))
        {
            return AgentToolResult.Failure("invalid_arguments", "报告周期、日期、格式、文件名和幂等键不能为空。");
        }

        var preview = await exportApi.PreviewAsync(new AgentReportExportCommand(
            period,
            input.Date ?? string.Empty,
            input.FormatId,
            input.FileName,
            input.IdempotencyKey,
            StartDate: input.StartDate,
            EndDate: input.EndDate,
            SavedQueryId: input.SavedQueryId,
            TemplateId: input.TemplateId,
            IncludeSummary: input.IncludeSummary,
            IncludeTagStatistics: input.IncludeTagStatistics,
            IncludeDailyHours: input.IncludeDailyHours), cancellationToken);
        if (!preview.Succeeded || preview.Command is null || preview.PreviewVersion is null)
            return AgentToolResult.Failure(preview.ErrorCode ?? "preview_failed", preview.ErrorMessage ?? "报告导出预览失败。");

        var confirmationArguments = JsonSerializer.SerializeToElement(new
        {
            action = "export_report",
            period = preview.Command.Period.ToString(),
            preview.StartDate,
            preview.EndDate,
            preview.ItemCount,
            preview.TotalHours,
            preview.Command.FormatId,
            preview.Command.FileName,
            preview.Command.SavedQueryId,
            preview.Command.TemplateId,
            preview.Command.IncludeSummary,
            preview.Command.IncludeTagStatistics,
            preview.Command.IncludeDailyHours,
            destination = "DiaryApp AI 导出目录",
        });
        if (!await ConfirmedProgramWrite.RequestAsync(
                confirmationService,
                Descriptor,
                confirmationArguments,
                context,
                cancellationToken))
        {
            return AgentToolResult.Failure("user_rejected", "用户拒绝了报告导出。");
        }

        var result = await exportApi.ExportAsync(preview.Command, cancellationToken);
        if (!result.Succeeded)
            return AgentToolResult.Failure(result.ErrorCode ?? "export_failed", result.ErrorMessage ?? "报告导出失败。");
        return new AgentToolResult(
            true,
            JsonSerializer.Serialize(new
            {
                result.FileName,
                result.ItemCount,
                result.Duplicate,
                result.PreviewVersion,
                result.ArtifactId,
            }),
            EffectSummary: result.Duplicate
                ? $"幂等命中，未重复导出 {result.FileName}。"
                : $"已导出报告 {result.FileName}，包含 {result.ItemCount} 条事项。");
    }

    private static bool TryParsePeriod(string? value, out AgentReportPeriod period)
    {
        period = value?.Trim().ToLowerInvariant() switch
        {
            "day" => AgentReportPeriod.Day,
            "week" => AgentReportPeriod.Week,
            "month" => AgentReportPeriod.Month,
            "quarter" => AgentReportPeriod.Quarter,
            "year" => AgentReportPeriod.Year,
            "custom" => AgentReportPeriod.Custom,
            _ => (AgentReportPeriod)(-1),
        };
        return Enum.IsDefined(period);
    }

    private sealed record ReportExportArguments
    {
        public string Period { get; init; } = string.Empty;

        public string? Date { get; init; }

        public string? StartDate { get; init; }

        public string? EndDate { get; init; }

        public string? SavedQueryId { get; init; }

        public string? TemplateId { get; init; }

        public bool IncludeSummary { get; init; }

        public bool IncludeTagStatistics { get; init; }

        public bool IncludeDailyHours { get; init; }

        public string FormatId { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        public string IdempotencyKey { get; init; } = string.Empty;
    }
}

public sealed class ListReportArtifactsTool(IAgentReportExportApi exportApi) : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"limit":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.reports.artifacts.list",
        "diary_list_generated_reports",
        "列出已生成报表",
        "列出 AI 管理导出目录中登记的最近报表，不访问任意路径。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.ReadOnly,
        "diary.ai-agent",
        "ReportExport");

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        var limit = arguments.TryGetProperty("limit", out var value) ? value.GetInt32() : 20;
        return ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(exportApi.ListArtifacts(limit))));
    }
}

public sealed class LocateReportArtifactTool(
    IAgentReportExportApi exportApi,
    IAgentConfirmationService confirmationService) : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"artifactId":{"type":"string"}},"required":["artifactId"],"additionalProperties":false}
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.reports.artifacts.locate",
        "diary_locate_generated_report",
        "定位已生成报表",
        "逐次确认后在文件管理器中定位已登记的报表；不能接受路径，也不能删除文件。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.Write,
        "diary.ai-agent",
        "ReportExport");

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("artifactId", out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            return AgentToolResult.Failure("invalid_arguments", "artifactId 不能为空。");
        var artifact = exportApi.ListArtifacts(100).FirstOrDefault(item => item.ArtifactId == value.GetString());
        if (artifact is null)
            return AgentToolResult.Failure("artifact_not_found", "报告记录不存在。");
        if (!await ConfirmedProgramWrite.RequestAsync(
                confirmationService,
                Descriptor,
                JsonSerializer.SerializeToElement(artifact),
                context,
                cancellationToken))
            return AgentToolResult.Failure("user_rejected", "用户拒绝定位报表。");
        var result = exportApi.LocateArtifact(artifact.ArtifactId);
        return result.Succeeded
            ? new AgentToolResult(true, JsonSerializer.Serialize(result), EffectSummary: $"已定位报表 {artifact.FileName}。")
            : AgentToolResult.Failure(result.ErrorCode ?? "artifact_locate_failed", result.ErrorMessage ?? "定位报表失败。");
    }
}
