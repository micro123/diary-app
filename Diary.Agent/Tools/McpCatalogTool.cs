using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.Agent.Mcp;

namespace Diary.Agent.Tools;

public sealed class ListConfiguredMcpServersTool(IMcpServerCatalog catalog) : IAgentTool
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
        additionalProperties = false,
    });

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.mcp.servers.list",
        "diary_list_mcp_servers",
        "列出已配置 MCP 服务",
        "列出 AI 助手设置中已保存的 MCP Server、运行状态和本地工具策略；不返回 Endpoint、命令、参数或凭据。",
        Schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.ReadOnly,
        "diary.ai-agent",
        "Mcp");

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var servers = catalog.ListConfiguredServers();
        return ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(new
        {
            count = servers.Count,
            servers,
        }, JsonOptions)));
    }
}
