using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Mcp;
using Diary.Mcp.Remote;
using Diary.ScriptBase;
using Diary.ScriptHost;
using Diary.Survey;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.ModuleTests;

[TestClass]
public sealed class RemoteMcpModuleTests
{
    [TestMethod]
    public void RateLimiterEnforcesPerPeerRequestsPerMinute()
    {
        var limiter = new PeerRequestRateLimiter(2);
        var peer = IPAddress.Parse("192.168.10.10");
        var now = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);

        Assert.IsTrue(limiter.TryAcquire(peer, now, out _));
        Assert.IsTrue(limiter.TryAcquire(peer, now.AddSeconds(1), out _));
        Assert.IsFalse(limiter.TryAcquire(peer, now.AddSeconds(2), out var retryAfter));
        Assert.IsGreaterThan(TimeSpan.FromSeconds(50), retryAfter);
        Assert.IsTrue(limiter.TryAcquire(peer, now.AddMinutes(1), out _));
    }

    [TestMethod]
    public async Task HttpServerPublishesOnlyReadOnlyTools()
    {
        var policy = new SurveyMcpPeerAccessPolicy(() => null, () => [IPAddress.Loopback]);
        var tools = new RemoteDiaryTools(
            new EmptyWorkItemQueryApi(),
            new EmptyWorkTagApi(),
            new EmptyExtraFieldApi(),
            new CurrentContextApi());
        await using var server = new McpHttpServer(
            policy,
            new PeerRequestRateLimiter(60),
            tools,
            NullLogger.Instance);
        server.Start();
        using var client = new HttpClient();
        var endpoint = new Uri($"http://127.0.0.1:{server.Port}/mcp");

        var requestJson = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list",
            @params = new { },
        });
        using var response = await client.PostAsync(
            endpoint,
            new StringContent(requestJson, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var publishedTools = document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();

        Assert.HasCount(5, publishedTools);
        Assert.IsTrue(publishedTools.All(tool => tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));
        Assert.IsTrue(publishedTools.All(tool => !tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean()));
        Assert.IsFalse(publishedTools.Any(tool => tool.GetProperty("name").GetString()?.Contains("create") == true));
        Assert.IsFalse(publishedTools.Any(tool => tool.GetProperty("name").GetString()?.Contains("update") == true));
    }

    [TestMethod]
    public async Task DiaryAgentClientCompletesInitializeAndListsRemoteTools()
    {
        var policy = new SurveyMcpPeerAccessPolicy(() => null, () => [IPAddress.Loopback]);
        var tools = new RemoteDiaryTools(
            new EmptyWorkItemQueryApi(),
            new EmptyWorkTagApi(),
            new EmptyExtraFieldApi(),
            new CurrentContextApi());
        await using var server = new McpHttpServer(
            policy,
            new PeerRequestRateLimiter(60),
            tools,
            NullLogger.Instance);
        server.Start();
        await using var connection = new McpClientConnection(
            new McpServerProfile
            {
                Id = "test.remote",
                DisplayName = "测试远程 MCP",
                Enabled = true,
                Transport = McpTransportKind.StreamableHttp,
                Endpoint = new Uri($"http://127.0.0.1:{server.Port}/mcp"),
                Authentication = new AiAuthenticationConfiguration { Kind = AiAuthenticationKind.None },
                Proxy = new AiProxyConfiguration { Mode = AiProxyMode.Direct },
            },
            new EmptyCredentialStore());

        var remoteTools = await connection.ListToolsAsync();

        Assert.HasCount(5, remoteTools);
        CollectionAssert.Contains(remoteTools.Select(tool => tool.Name).ToArray(), "diary_query_work_items");
        Assert.IsFalse(remoteTools.Any(tool => tool.Name.Contains("create", StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class EmptyWorkItemQueryApi : IWorkItemQueryScriptApi
    {
        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(
            ScriptWorkItemQuery query,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            ScriptWorkItemQueryResult.Success(ImmutableArray<ScriptWorkItem>.Empty, query));
    }

    private sealed class EmptyWorkTagApi : IWorkTagScriptApi
    {
        public IReadOnlyList<ScriptWorkTagInfo> List() => [];
    }

    private sealed class EmptyExtraFieldApi : ITagExtraFieldScriptApi
    {
        public IReadOnlyList<ScriptTagExtraFieldInfo> List(bool includeDisabled = false) => [];
    }

    private sealed class CurrentContextApi : ICurrentContextScriptApi
    {
        public ScriptCurrentContext Get() => new("2026-10-10", null, null);
    }

    private sealed class EmptyCredentialStore : IAiCredentialStore
    {
        public ValueTask<bool> ExistsAsync(string reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public ValueTask<CredentialValue?> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CredentialValue?>(null);

        public ValueTask SetAsync(
            string reference,
            ReadOnlyMemory<char> value,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string reference, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
