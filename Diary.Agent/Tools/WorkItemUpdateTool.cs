using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.ScriptHost;

namespace Diary.Agent.Tools;

public sealed class WorkItemUpdateTool(
    IWorkItemCommandApi commandApi,
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
            "workItemId":{"type":"integer","minimum":1},
            "date":{"type":["string","null"],"description":"yyyy-MM-dd"},
            "title":{"type":["string","null"]},
            "hours":{"type":["number","null"],"exclusiveMinimum":0,"maximum":24},
            "priority":{"type":["integer","null"],"minimum":0,"maximum":9},
            "tagIds":{"type":["array","null"],"items":{"type":"integer"}},
            "extraFields":{"type":["array","null"],"items":{"type":"object","properties":{"fieldId":{"type":"string"},"value":{"type":"string"}},"required":["fieldId","value"],"additionalProperties":false}},
            "idempotencyKey":{"type":"string"}
          },
          "required":["workItemId","idempotencyKey"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.work-items.update",
        "diary_update_work_item",
        "更新事项",
        "预览指定事项的字段差异，并且仅在用户逐次确认后更新；支持标签附加字段值，不能修改本地备注或删除事项。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.Write,
        "diary.ai-agent");

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        WorkItemUpdateArguments? input;
        try
        {
            input = arguments.Deserialize<WorkItemUpdateArguments>(JsonOptions);
        }
        catch (JsonException)
        {
            return AgentToolResult.Failure("invalid_arguments", "事项更新参数格式无效。");
        }
        if (input is null || input.WorkItemId <= 0 || string.IsNullOrWhiteSpace(input.IdempotencyKey))
            return AgentToolResult.Failure("invalid_arguments", "workItemId 和 idempotencyKey 不能为空。");
        if (input.Date is null
            && input.Title is null
            && input.Hours is null
            && input.Priority is null
            && input.TagIds is null
            && input.ExtraFields is null)
        {
            return AgentToolResult.Failure("invalid_arguments", "至少需要提供一个待更新字段。");
        }

        var preview = await commandApi.PreviewUpdateAsync(new WorkItemUpdateCommand(
            input.WorkItemId,
            input.Date,
            input.Title,
            input.Hours,
            input.Priority,
            input.TagIds,
            input.IdempotencyKey,
            input.ExtraFields), cancellationToken);
        if (!preview.Succeeded || preview.Before is null || preview.Command is null || preview.PreviewVersion is null)
            return AgentToolResult.Failure(preview.ErrorCode ?? "preview_failed", preview.ErrorMessage ?? "事项更新预览失败。");

        var confirmationArguments = JsonSerializer.SerializeToElement(new
        {
            action = "update_work_item",
            before = preview.Before,
            after = new
            {
                preview.Command.WorkItemId,
                preview.Command.Date,
                preview.Command.Title,
                preview.Command.Hours,
                preview.Command.Priority,
                preview.Command.TagIds,
                preview.Command.ExtraFields,
            },
        });
        if (!await ConfirmedProgramWrite.RequestAsync(
                confirmationService,
                Descriptor,
                confirmationArguments,
                context,
                cancellationToken))
        {
            return AgentToolResult.Failure("user_rejected", "用户拒绝了事项更新。");
        }

        var result = await commandApi.UpdateAsync(preview.Command, cancellationToken);
        if (!result.Succeeded)
            return AgentToolResult.Failure(result.ErrorCode ?? "update_failed", result.ErrorMessage ?? "更新事项失败。");
        return new AgentToolResult(
            true,
            JsonSerializer.Serialize(new
            {
                result.WorkItemId,
                result.Duplicate,
                result.PreviewVersion,
            }),
            EffectSummary: result.Duplicate
                ? $"幂等命中，事项 {result.WorkItemId} 未重复更新。"
                : $"已更新事项 {result.WorkItemId}。");
    }

    private sealed record WorkItemUpdateArguments
    {
        public int WorkItemId { get; init; }

        public string? Date { get; init; }

        public string? Title { get; init; }

        public double? Hours { get; init; }

        public int? Priority { get; init; }

        public IReadOnlyList<int>? TagIds { get; init; }

        public IReadOnlyList<WorkItemExtraFieldCommand>? ExtraFields { get; init; }

        public string IdempotencyKey { get; init; } = string.Empty;
    }
}
