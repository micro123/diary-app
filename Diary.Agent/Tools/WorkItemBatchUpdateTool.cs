using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.ScriptHost;

namespace Diary.Agent.Tools;

public sealed class WorkItemBatchUpdateTool(
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
            "updates":{
              "type":"array","minItems":1,"maxItems":20,
              "items":{
                "type":"object",
                "properties":{
                  "workItemId":{"type":"integer","minimum":1},
                  "date":{"type":["string","null"]},
                  "title":{"type":["string","null"],"maxLength":500},
                  "hours":{"type":["number","null"],"exclusiveMinimum":0,"maximum":24},
                  "priority":{"type":["integer","null"],"minimum":0,"maximum":9},
                  "tagIds":{"type":["array","null"],"items":{"type":"integer"}},
                  "extraFields":{"type":["array","null"],"items":{"type":"object","properties":{"fieldId":{"type":"string"},"value":{"type":"string"}},"required":["fieldId","value"],"additionalProperties":false}}
                },
                "required":["workItemId"],
                "additionalProperties":false
              }
            },
            "idempotencyKey":{"type":"string","maxLength":194}
          },
          "required":["updates","idempotencyKey"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.work-items.batch-update",
        "diary_batch_update_work_items",
        "批量更新事项",
        "原子预览并更新最多 20 个事项，支持标签附加字段值；仅在用户逐次确认后执行，不修改本地备注或删除事项。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.Write,
        "diary.ai-agent");

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        BatchUpdateArguments? input;
        try
        {
            input = arguments.Deserialize<BatchUpdateArguments>(JsonOptions);
        }
        catch (JsonException)
        {
            return AgentToolResult.Failure("invalid_arguments", "批量事项更新参数格式无效。");
        }
        if (input is null
            || input.Updates.Count is 0 or > 20
            || string.IsNullOrWhiteSpace(input.IdempotencyKey))
        {
            return AgentToolResult.Failure("invalid_arguments", "updates 必须包含 1 到 20 项，idempotencyKey 不能为空。");
        }
        if (input.Updates.Any(update => !update.HasChanges))
            return AgentToolResult.Failure("invalid_arguments", "批量更新中的每个事项都必须包含至少一个待更新字段。");

        var command = new WorkItemBatchUpdateCommand(
            input.Updates.Select(update => new WorkItemUpdateCommand(
                update.WorkItemId,
                update.Date,
                update.Title,
                update.Hours,
                update.Priority,
                update.TagIds,
                string.Empty,
                update.ExtraFields)).ToArray(),
            input.IdempotencyKey);
        var preview = await commandApi.PreviewBatchUpdateAsync(command, cancellationToken);
        if (!preview.Succeeded || preview.Command is null || preview.PreviewVersion is null)
            return AgentToolResult.Failure(preview.ErrorCode ?? "preview_failed", preview.ErrorMessage ?? "批量事项更新预览失败。");

        var confirmationArguments = JsonSerializer.SerializeToElement(new
        {
            action = "batch_update_work_items",
            count = preview.Items.Count,
            items = preview.Items.Select(item => new
            {
                before = item.Before,
                after = item.Command is null
                    ? null
                    : new
                    {
                        item.Command.WorkItemId,
                        item.Command.Date,
                        item.Command.Title,
                        item.Command.Hours,
                        item.Command.Priority,
                        item.Command.TagIds,
                        item.Command.ExtraFields,
                    },
            }),
        });
        if (!await ConfirmedProgramWrite.RequestAsync(
                confirmationService,
                Descriptor,
                confirmationArguments,
                context,
                cancellationToken))
        {
            return AgentToolResult.Failure("user_rejected", "用户拒绝了批量事项更新。");
        }

        var result = await commandApi.BatchUpdateAsync(preview.Command, cancellationToken);
        if (!result.Succeeded)
            return AgentToolResult.Failure(result.ErrorCode ?? "batch_update_failed", result.ErrorMessage ?? "批量更新事项失败。");
        return new AgentToolResult(
            true,
            JsonSerializer.Serialize(new
            {
                result.WorkItemIds,
                count = result.WorkItemIds.Count,
                result.Duplicate,
                result.PreviewVersion,
            }),
            EffectSummary: result.Duplicate
                ? $"幂等命中，{result.WorkItemIds.Count} 个事项未重复更新。"
                : $"已批量更新 {result.WorkItemIds.Count} 个事项。");
    }

    private sealed record BatchUpdateArguments
    {
        public IReadOnlyList<BatchUpdateItemArguments> Updates { get; init; } = [];

        public string IdempotencyKey { get; init; } = string.Empty;
    }

    private sealed record BatchUpdateItemArguments
    {
        public int WorkItemId { get; init; }

        public string? Date { get; init; }

        public string? Title { get; init; }

        public double? Hours { get; init; }

        public int? Priority { get; init; }

        public IReadOnlyList<int>? TagIds { get; init; }

        public IReadOnlyList<WorkItemExtraFieldCommand>? ExtraFields { get; init; }

        public bool HasChanges => Date is not null
                                  || Title is not null
                                  || Hours is not null
                                  || Priority is not null
                                  || TagIds is not null
                                  || ExtraFields is not null;
    }
}
