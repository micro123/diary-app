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
            "period":{"type":"string","enum":["day","week"]},
            "date":{"type":"string","description":"日报日期或周报所在周的任意日期，yyyy-MM-dd"},
            "formatId":{"type":"string"},
            "fileName":{"type":"string"},
            "idempotencyKey":{"type":"string"}
          },
          "required":["period","date","formatId","fileName","idempotencyKey"],
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
        "diary.ai-agent");

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
            || string.IsNullOrWhiteSpace(input.Date)
            || string.IsNullOrWhiteSpace(input.FormatId)
            || string.IsNullOrWhiteSpace(input.FileName)
            || string.IsNullOrWhiteSpace(input.IdempotencyKey))
        {
            return AgentToolResult.Failure("invalid_arguments", "报告周期、日期、格式、文件名和幂等键不能为空。");
        }

        var preview = await exportApi.PreviewAsync(new AgentReportExportCommand(
            period,
            input.Date,
            input.FormatId,
            input.FileName,
            input.IdempotencyKey), cancellationToken);
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
            _ => (AgentReportPeriod)(-1),
        };
        return Enum.IsDefined(period);
    }

    private sealed record ReportExportArguments
    {
        public string Period { get; init; } = string.Empty;

        public string Date { get; init; } = string.Empty;

        public string FormatId { get; init; } = string.Empty;

        public string FileName { get; init; } = string.Empty;

        public string IdempotencyKey { get; init; } = string.Empty;
    }
}
