using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Mcp;
using Diary.Agent.Tools;

namespace Diary.AgentTests;

[TestClass]
public sealed class McpCatalogToolTests
{
    [TestMethod]
    public async Task ListsConfiguredServersWithoutConnectionOrCredentialDetails()
    {
        var tool = new ListConfiguredMcpServersTool(new FakeCatalog());

        var result = await tool.InvokeAsync(
            JsonSerializer.SerializeToElement(new { }),
            new AgentToolInvocationContext(Guid.NewGuid(), Guid.NewGuid(), EmptyServiceProvider.Instance));

        Assert.IsTrue(result.Succeeded);
        using var document = JsonDocument.Parse(result.Content);
        var server = document.RootElement.GetProperty("servers")[0];
        Assert.AreEqual("company-data", server.GetProperty("serverId").GetString());
        Assert.AreEqual("公司知识库", server.GetProperty("displayName").GetString());
        Assert.AreEqual("Connected", server.GetProperty("state").GetString());
        Assert.AreEqual("search_documents", server.GetProperty("tools")[0].GetProperty("toolName").GetString());
        Assert.IsFalse(result.Content.Contains("https://", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Content.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Content.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Content.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void UsesMcpPermissionGroup()
    {
        var descriptor = new ListConfiguredMcpServersTool(new FakeCatalog()).Descriptor;

        Assert.AreEqual("Mcp", descriptor.PermissionGroup);
        Assert.AreEqual(AgentToolRisk.ReadOnly, descriptor.Risk);
    }

    private sealed class FakeCatalog : IMcpServerCatalog
    {
        public IReadOnlyList<McpConfiguredServerSummary> ListConfiguredServers() =>
        [
            new(
                "company-data",
                "公司知识库",
                true,
                McpTransportKind.StreamableHttp,
                McpServerRuntimeState.Connected,
                [new McpConfiguredToolSummary("search_documents", true, AgentToolRisk.ReadOnly)],
                2,
                1,
                false,
                true,
                false,
                new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero)),
        ];
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
