using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.Agent.Runtime;

namespace Diary.Agent.Tools;

public sealed class AgentMemoryTool(
    AgentMemoryStore memoryStore,
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
            "content":{"type":"string","description":"更新后的完整 Markdown 记忆内容，允许为空以清空记忆"},
            "reason":{"type":"string","minLength":1,"maxLength":500,"description":"本次更新原因，必须对应用户明确表达的长期规则、偏好或背景"}
          },
          "required":["content","reason"],
          "additionalProperties":false
        }
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.agent-memory.update",
        "diary_update_agent_memory",
        "更新 Agent 长期记忆",
        "提议替换 DiaryApp 固定记忆文件的完整 Markdown 内容。仅记录用户明确要求长期记住的规则、偏好和背景；必须逐次获得用户确认，不能指定路径或读取其他文件。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.Write,
        "diary.ai-agent",
        "AgentMemoryWrite");

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        AgentMemoryUpdateInput? input;
        try
        {
            input = arguments.Deserialize<AgentMemoryUpdateInput>(JsonOptions);
        }
        catch (JsonException)
        {
            return AgentToolResult.Failure("invalid_arguments", "Agent 记忆更新参数缺失或格式无效。");
        }
        if (input is null || input.Content is null || string.IsNullOrWhiteSpace(input.Reason))
            return AgentToolResult.Failure("invalid_arguments", "Agent 记忆内容和更新原因不能为空。");
        if (input.Reason.Length > 500)
            return AgentToolResult.Failure("invalid_arguments", "Agent 记忆更新原因不能超过 500 个字符。");

        AgentMemorySnapshot before;
        try
        {
            before = await memoryStore.LoadAsync(cancellationToken);
            _ = AgentMemoryStore.GetUtf8ByteCount(input.Content);
        }
        catch (AgentMemoryException exception)
        {
            return AgentToolResult.Failure(exception.Code, exception.Message);
        }

        var confirmationArguments = JsonSerializer.SerializeToElement(new
        {
            input.Reason,
            PreviousBytes = before.ByteLength,
            UpdatedBytes = AgentMemoryStore.GetUtf8ByteCount(input.Content),
        }, JsonOptions);
        var decision = await confirmationService.ConfirmExternalToolAsync(
            new ExternalToolConfirmationRequest(
                Guid.NewGuid(),
                context.RunId,
                context.InvocationId,
                "DiaryApp Agent 记忆",
                $"{Descriptor.DisplayName} ({Descriptor.ModelName})",
                confirmationArguments,
                "确认更新 Agent 长期记忆",
                BuildPreview(input.Reason, before.Content, input.Content)),
            cancellationToken);
        if (decision != AgentConfirmationDecision.Confirm)
            return AgentToolResult.Failure("user_rejected", "用户拒绝了 Agent 记忆更新。");

        try
        {
            var saved = await memoryStore.SaveAsync(input.Content, before.Version, cancellationToken);
            return new AgentToolResult(
                true,
                JsonSerializer.Serialize(new
                {
                    Updated = true,
                    saved.ByteLength,
                    saved.LastWriteTimeUtc,
                }, JsonOptions),
                Source: "DiaryApp Agent 记忆",
                EffectSummary: $"已更新 Agent 长期记忆，共 {saved.ByteLength} 字节。");
        }
        catch (AgentMemoryException exception)
        {
            return AgentToolResult.Failure(exception.Code, exception.Message);
        }
    }

    private static string BuildPreview(string reason, string before, string after) => $"""
        更新原因：
        {reason.Trim()}

        当前记忆：
        ────────────────────
        {(string.IsNullOrEmpty(before) ? "（空）" : before)}

        更新后：
        ────────────────────
        {(string.IsNullOrEmpty(after) ? "（空）" : after)}
        """;

    private sealed record AgentMemoryUpdateInput(string? Content, string? Reason);
}
