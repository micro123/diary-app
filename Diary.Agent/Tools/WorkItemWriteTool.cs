using System.Text.Json;
using Diary.ScriptHost;

namespace Diary.Agent.Tools;

public sealed class WorkItemWriteTool(
    IWorkItemCommandApi commandApi,
    IAgentConfirmationService confirmationService) : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "date":{"type":"string","description":"yyyy-MM-dd"},
            "title":{"type":"string"},
            "hours":{"type":"number","exclusiveMinimum":0,"maximum":24},
            "priority":{"type":"integer","minimum":0,"maximum":9},
            "tagIds":{"type":"array","items":{"type":"integer"}},
            "extraFields":{"type":"array","items":{"type":"object","properties":{"fieldId":{"type":"string"},"value":{"type":"string"}},"required":["fieldId","value"],"additionalProperties":false}},
            "note":{"type":["string","null"]},
            "idempotencyKey":{"type":"string"}
          },
          "required":["date","title","hours","priority","tagIds","extraFields","idempotencyKey"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.work-items.create",
        "diary_create_work_item",
        "创建事项",
        "预览一个事项，并且仅在用户明确确认后创建。不得代替用户确认。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.Write,
        "diary.ai-agent");

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!TryParse(arguments, out var command, out var error))
            return AgentToolResult.Failure("invalid_arguments", error);
        var preview = await commandApi.PreviewCreateAsync(command!, cancellationToken);
        if (!preview.Succeeded || preview.Command is null || preview.PreviewVersion is null)
            return AgentToolResult.Failure(preview.ErrorCode ?? "preview_failed", preview.ErrorMessage ?? "事项预览失败。");

        var confirmation = await confirmationService.ConfirmWorkItemAsync(
            new WorkItemConfirmationRequest(
                Guid.NewGuid(),
                context.RunId,
                context.InvocationId,
                preview.Command,
                preview.PreviewVersion),
            cancellationToken);
        if (confirmation.Decision == AgentConfirmationDecision.Reject)
            return AgentToolResult.Failure("user_rejected", "用户拒绝了事项写入。");

        var confirmedCommand = preview.Command;
        if (confirmation.Decision == AgentConfirmationDecision.EditAndConfirm)
        {
            if (confirmation.EditedCommand is null)
                return AgentToolResult.Failure("edited_command_missing", "编辑后确认缺少事项内容。");
            var editedPreview = await commandApi.PreviewCreateAsync(confirmation.EditedCommand, cancellationToken);
            if (!editedPreview.Succeeded || editedPreview.Command is null)
                return AgentToolResult.Failure(editedPreview.ErrorCode ?? "preview_failed", editedPreview.ErrorMessage ?? "编辑后的事项预览失败。");
            confirmedCommand = editedPreview.Command;
        }

        var result = await commandApi.CreateAsync(confirmedCommand, cancellationToken);
        if (!result.Succeeded)
            return AgentToolResult.Failure(result.ErrorCode ?? "create_failed", result.ErrorMessage ?? "创建事项失败。");
        return new AgentToolResult(
            true,
            JsonSerializer.Serialize(new
            {
                result.WorkItemId,
                result.Duplicate,
                result.PreviewVersion,
            }),
            EffectSummary: result.Duplicate
                ? $"幂等命中，事项 {result.WorkItemId} 未重复创建。"
                : $"已创建事项 {result.WorkItemId}。");
    }

    private static bool TryParse(JsonElement arguments, out WorkItemCreateCommand? command, out string error)
    {
        command = null;
        error = string.Empty;
        try
        {
            var date = arguments.GetProperty("date").GetString() ?? string.Empty;
            var title = arguments.GetProperty("title").GetString() ?? string.Empty;
            var hours = arguments.GetProperty("hours").GetDouble();
            var priority = arguments.GetProperty("priority").GetInt32();
            var tagIds = arguments.GetProperty("tagIds").EnumerateArray().Select(item => item.GetInt32()).ToArray();
            var extraFields = arguments.GetProperty("extraFields").EnumerateArray()
                .Select(item => new WorkItemExtraFieldCommand(
                    item.GetProperty("fieldId").GetString() ?? string.Empty,
                    item.GetProperty("value").GetString() ?? string.Empty))
                .ToArray();
            var note = arguments.TryGetProperty("note", out var noteElement)
                       && noteElement.ValueKind == JsonValueKind.String
                ? noteElement.GetString()
                : null;
            var idempotencyKey = arguments.GetProperty("idempotencyKey").GetString() ?? string.Empty;
            command = new WorkItemCreateCommand(
                date,
                title,
                hours,
                priority,
                tagIds,
                extraFields,
                note,
                idempotencyKey);
            return true;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            error = "事项参数缺失或类型无效。";
            return false;
        }
    }
}
