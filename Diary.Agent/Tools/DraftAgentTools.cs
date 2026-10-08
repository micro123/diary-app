using System.Text.Json;

namespace Diary.Agent.Tools;

public sealed class WorkItemDraftTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"date":{"type":"string"},"title":{"type":"string"},"hours":{"type":"number"},"priority":{"type":"integer"},"tagIds":{"type":"array","items":{"type":"integer"}},"extraFields":{"type":"array","items":{"type":"object"}},"note":{"type":["string","null"]}},"required":["date","title","hours","priority","tagIds","extraFields"],"additionalProperties":false}
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.drafts.work-item",
        "diary_draft_work_item",
        "生成事项草稿",
        "生成结构化事项草稿，不访问数据库也不产生写入。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.ReadOnly,
        "diary.ai-agent");

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(new
        {
            kind = "work_item",
            draft = arguments,
            requiresConfirmationToWrite = true,
        })));
}

public sealed class ReportDraftTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"kind":{"type":"string","enum":["daily","weekly"]},"startDate":{"type":"string"},"endDate":{"type":"string"},"title":{"type":"string"},"sections":{"type":"array","items":{"type":"object","properties":{"heading":{"type":"string"},"content":{"type":"string"}},"required":["heading","content"],"additionalProperties":false}}},"required":["kind","startDate","endDate","title","sections"],"additionalProperties":false}
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.drafts.report",
        "diary_draft_report",
        "生成日报或周报草稿",
        "生成结构化日报或周报草稿，不导出文件也不写入数据库。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.ReadOnly,
        "diary.ai-agent");

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(new
        {
            kind = "report",
            draft = arguments,
        })));
}

public sealed class ScriptDraftTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"language":{"type":"string","enum":["csharp","lua","python"]},"fileName":{"type":"string"},"purpose":{"type":"string"},"source":{"type":"string"}},"required":["language","fileName","purpose","source"],"additionalProperties":false}
        """).RootElement.Clone();

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.drafts.script",
        "diary_draft_script",
        "生成脚本草稿",
        "生成结构化脚本草稿；不会保存或执行脚本，可配合脚本校验工具检查。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.ReadOnly,
        "diary.ai-agent");

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(new
        {
            kind = "script",
            draft = arguments,
            executable = false,
        })));
}
