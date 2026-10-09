using System.Text.Json;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.Agent.Tools;

internal static class ConfirmedProgramWrite
{
    public static async ValueTask<bool> RequestAsync(
        IAgentConfirmationService confirmationService,
        AgentToolDescriptor descriptor,
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        var decision = await confirmationService.ConfirmExternalToolAsync(
            new ExternalToolConfirmationRequest(
                Guid.NewGuid(),
                context.RunId,
                context.InvocationId,
                "DiaryApp 内置工具",
                $"{descriptor.DisplayName} ({descriptor.ModelName})",
                arguments.Clone()),
            cancellationToken);
        return decision == AgentConfirmationDecision.Confirm;
    }
}

public sealed class TemplateWorkItemWriteTool(
    ITemplateLogItemScriptApi templateApi,
    ITemplateScriptApi templateDiscovery,
    IAgentConfirmationService confirmationService,
    IWorkItemAutomationPublisher? automationPublisher = null) : IAgentTool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "date":{"type":"string","description":"yyyy-MM-dd"},
            "templateId":{"type":"string","description":"模板 UUID"},
            "hours":{"type":"number","exclusiveMinimum":0,"maximum":24},
            "title":{"type":["string","null"]},
            "note":{"type":["string","null"]},
            "idempotencyKey":{"type":"string","minLength":1,"maxLength":128}
          },
          "required":["date","templateId","hours","idempotencyKey"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.work-items.create-from-template",
        "diary_create_from_template",
        "从模板创建事项",
        "先预览模板创建结果，并且仅在用户逐次确认后创建事项。不得代替用户确认。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.Write,
        "diary.ai-agent");

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        TemplateWriteInput? input;
        try
        {
            input = arguments.Deserialize<TemplateWriteInput>(JsonOptions);
        }
        catch (JsonException)
        {
            return AgentToolResult.Failure("invalid_arguments", "模板事项参数缺失或类型无效。");
        }
        if (input is null || string.IsNullOrWhiteSpace(input.IdempotencyKey))
            return AgentToolResult.Failure("invalid_arguments", "模板事项参数无效，幂等键不能为空。");
        var template = templateDiscovery.List().FirstOrDefault(item =>
            string.Equals(item.Id, input.TemplateId, StringComparison.OrdinalIgnoreCase));
        if (template is null)
            return AgentToolResult.Failure("invalid_arguments", "指定的模板不存在。");

        var request = new ScriptTemplateLogItemRequest(
            input.Date,
            input.TemplateId,
            input.Hours,
            input.Title,
            input.Note,
            input.IdempotencyKey,
            Preview: true);
        var preview = await templateApi.CreateAsync(request, cancellationToken);
        if (!preview.Succeeded || preview.Item is null)
            return Failure(preview, "模板事项预览失败。");

        var confirmationArguments = JsonSerializer.SerializeToElement(new
        {
            input.Date,
            input.TemplateId,
            input.Hours,
            input.Title,
            input.Note,
            input.IdempotencyKey,
            Template = new
            {
                template.Id,
                template.Name,
                template.DefaultTitle,
                template.DefaultHours,
                template.DefaultWorkTagIds,
            },
            Preview = new
            {
                preview.Item.Comment,
                preview.Item.Hours,
                preview.Item.Date,
            },
        }, JsonOptions);
        if (!await ConfirmedProgramWrite.RequestAsync(
                confirmationService,
                Descriptor,
                confirmationArguments,
                context,
                cancellationToken))
        {
            return AgentToolResult.Failure("user_rejected", "用户拒绝了模板事项写入。");
        }

        var refreshedPreview = await templateApi.CreateAsync(request, cancellationToken);
        if (!refreshedPreview.Succeeded)
            return Failure(refreshedPreview, "确认后的模板事项预览失败。");
        var result = await templateApi.CreateAsync(request with { Preview = false }, cancellationToken);
        if (!result.Succeeded || result.Item is null)
            return Failure(result, "按模板创建事项失败。");
        if (!result.Duplicate)
            PublishAutomation(result.Item);
        return new AgentToolResult(
            true,
            JsonSerializer.Serialize(new
            {
                result.Item.Id,
                result.Item.Date,
                Title = result.Item.Comment,
                result.Item.Hours,
                result.Duplicate,
            }),
            EffectSummary: result.Duplicate
                ? $"幂等命中，事项 {result.Item.Id} 未重复创建。"
                : $"已从模板创建事项 {result.Item.Id}。");
    }

    private static AgentToolResult Failure(ScriptLogItemResult result, string fallback) =>
        AgentToolResult.Failure(
            result.ApiError?.Code ?? "template_create_failed",
            result.Error?.Message ?? fallback);

    private void PublishAutomation(ScriptWorkItem item)
    {
        if (automationPublisher is null)
            return;
        try
        {
            automationPublisher.Publish(new WorkItemAutomationEvent(
                ScriptAutomationTriggerKind.WorkItemCreated,
                item.Id,
                item.Date,
                item.Comment,
                item.Hours,
                item.Priority));
            for (var index = 0; index < item.Tags.Length; index++)
            {
                var tag = item.Tags[index];
                automationPublisher.Publish(new WorkItemAutomationEvent(
                    ScriptAutomationTriggerKind.TagAdded,
                    item.Id,
                    item.Date,
                    item.Comment,
                    item.Hours,
                    item.Priority,
                    tag.Id,
                    tag.Name,
                    tag.Level,
                    "Agent",
                    index));
            }
        }
        catch
        {
        }
    }

    private sealed record TemplateWriteInput(
        string Date,
        string TemplateId,
        double Hours,
        string? Title,
        string? Note,
        string IdempotencyKey);
}

public sealed class ClipboardWriteTool(
    IClipboardScriptApi clipboardApi,
    IAgentConfirmationService confirmationService) : IAgentTool
{
    private const int MaxTextLength = 20_000;
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{"text":{"type":"string","maxLength":20000}},
          "required":["text"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.clipboard.set",
        "diary_set_clipboard_text",
        "写入剪贴板",
        "仅在用户逐次确认后，将指定文本写入系统剪贴板。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.Write,
        "diary.ai-agent");

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("text", out var textElement)
            || textElement.ValueKind != JsonValueKind.String)
        {
            return AgentToolResult.Failure("invalid_arguments", "剪贴板文本参数缺失或类型无效。");
        }
        var text = textElement.GetString() ?? string.Empty;
        if (text.Length > MaxTextLength)
            return AgentToolResult.Failure("invalid_arguments", $"剪贴板文本不能超过 {MaxTextLength} 个字符。");
        if (!await ConfirmedProgramWrite.RequestAsync(
                confirmationService,
                Descriptor,
                arguments,
                context,
                cancellationToken))
        {
            return AgentToolResult.Failure("user_rejected", "用户拒绝了剪贴板写入。");
        }
        if (!await clipboardApi.SetTextAsync(text, cancellationToken))
            return AgentToolResult.Failure("clipboard_unavailable", "当前系统剪贴板不可用。");
        return new AgentToolResult(
            true,
            JsonSerializer.Serialize(new { CharacterCount = text.Length }),
            EffectSummary: $"已向剪贴板写入 {text.Length} 个字符。");
    }
}

public sealed class AppNotificationWriteTool(
    IUserInteractionScriptApi interactionApi,
    IAgentConfirmationService confirmationService) : IAgentTool
{
    private const int MaxTitleLength = 100;
    private const int MaxBodyLength = 2_000;
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "title":{"type":"string","minLength":1,"maxLength":100},
            "body":{"type":"string","minLength":1,"maxLength":2000}
          },
          "required":["title","body"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.ui.notify",
        "diary_notify",
        "发送应用通知",
        "仅在用户逐次确认后，在 DiaryApp 中显示一条会话级通知。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.Write,
        "diary.ai-agent");

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!TryRead(arguments, "title", MaxTitleLength, out var title)
            || !TryRead(arguments, "body", MaxBodyLength, out var body))
        {
            return AgentToolResult.Failure("invalid_arguments", "通知标题或正文为空、类型无效或超过长度限制。");
        }
        if (!await ConfirmedProgramWrite.RequestAsync(
                confirmationService,
                Descriptor,
                arguments,
                context,
                cancellationToken))
        {
            return AgentToolResult.Failure("user_rejected", "用户拒绝了应用通知。");
        }
        await interactionApi.NotifyAsync(title, body, cancellationToken);
        return new AgentToolResult(
            true,
            JsonSerializer.Serialize(new { title, Displayed = true }),
            EffectSummary: $"已显示应用通知“{title}”。");
    }

    private static bool TryRead(JsonElement arguments, string name, int maxLength, out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;
    }
}
