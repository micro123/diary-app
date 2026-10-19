using System.Runtime.CompilerServices;
using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Protocols;
using Diary.Agent.Runtime;
using Diary.Agent.Tools;

namespace Diary.AgentTests;

[TestClass]
public sealed class AgentRuntimeContextTests
{
    private static readonly TimeZoneInfo TestTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Shanghai",
        TimeSpan.FromHours(8),
        "Test Shanghai",
        "Test Shanghai");

    [TestMethod]
    public void RuntimeContextUsesConfiguredTimezoneAndRelativeDates()
    {
        var provider = new MutableTimeProvider(
            new DateTimeOffset(2026, 10, 18, 16, 30, 45, TimeSpan.Zero),
            TestTimeZone);

        var snapshot = AgentRuntimeContextSnapshot.Capture(provider);

        Assert.AreEqual("2026-10-19", snapshot.LocalDate);
        Assert.AreEqual("Monday", snapshot.DayOfWeek);
        Assert.AreEqual("Test/Shanghai", snapshot.TimeZoneId);
        Assert.AreEqual("+08:00", snapshot.UtcOffset);
        Assert.AreEqual("2026-10-18", snapshot.Yesterday);
        Assert.AreEqual("2026-10-20", snapshot.Tomorrow);
        StringAssert.Contains(snapshot.ToSystemBlock(), "2026-10-19T00:30:45+08:00");
    }

    [TestMethod]
    public async Task CurrentTimeToolReturnsFreshReadOnlySnapshot()
    {
        var provider = new MutableTimeProvider(
            new DateTimeOffset(2026, 10, 18, 16, 30, 45, TimeSpan.Zero),
            TestTimeZone);
        var tool = new GetCurrentTimeTool(provider);
        using var arguments = JsonDocument.Parse("{}");

        var first = await tool.InvokeAsync(arguments.RootElement, CreateContext());
        provider.SetUtcNow(new DateTimeOffset(2026, 10, 19, 16, 30, 45, TimeSpan.Zero));
        var second = await tool.InvokeAsync(arguments.RootElement, CreateContext());

        Assert.IsTrue(first.Succeeded);
        Assert.IsTrue(second.Succeeded);
        Assert.AreEqual(AgentToolRisk.ReadOnly, tool.Descriptor.Risk);
        Assert.IsTrue(AgentToolPermissionPolicy.IsEnabled(tool.Descriptor, new AiAgentToolSettings()));
        using var firstDocument = JsonDocument.Parse(first.Content);
        using var secondDocument = JsonDocument.Parse(second.Content);
        Assert.AreEqual("2026-10-19", firstDocument.RootElement.GetProperty("localDate").GetString());
        Assert.AreEqual("2026-10-20", secondDocument.RootElement.GetProperty("localDate").GetString());
    }

    [TestMethod]
    public async Task SessionRefreshesRuntimeContextForEveryRunWithoutPersistingIt()
    {
        var provider = new MutableTimeProvider(
            new DateTimeOffset(2026, 10, 18, 16, 30, 45, TimeSpan.Zero),
            TestTimeZone);
        var gateway = new CapturingGateway();
        var session = new AgentSessionService(
            gateway,
            new AgentToolExecutor(),
            EmptyServiceProvider.Instance,
            timeProvider: provider);
        var profile = CreateProfile();
        var snapshot = new AgentToolRegistry().CreateSnapshot();
        var options = new AgentRunOptions(SupportsTools: false);

        await session.RunAsync("first", profile, snapshot, options);
        provider.SetUtcNow(new DateTimeOffset(2026, 10, 19, 16, 30, 45, TimeSpan.Zero));
        await session.RunAsync("second", profile, snapshot, options);

        StringAssert.Contains(gateway.Requests[0].SystemInstruction, "current_local_date: 2026-10-19");
        StringAssert.Contains(gateway.Requests[1].SystemInstruction, "current_local_date: 2026-10-20");
        Assert.IsFalse(session.Messages.Any(message =>
            message.Text.Contains("current_local_date", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AllProtocolsPutRuntimeContextInSystemFieldBeforeMemoryAndSummary()
    {
        const string memoryMarker = "RUNTIME_ORDER_MEMORY";
        const string summaryMarker = "RUNTIME_ORDER_SUMMARY";
        var runtimeContext = AgentRuntimeContextSnapshot.Capture(new MutableTimeProvider(
            new DateTimeOffset(2026, 10, 18, 16, 30, 45, TimeSpan.Zero),
            TestTimeZone));
        var systemInstruction = AgentContextCompactor.BuildSystemInstruction(
            summaryMarker,
            memoryMarker,
            runtimeContext);
        Assert.IsTrue(systemInstruction.IndexOf("<runtime_context>", StringComparison.Ordinal)
                      < systemInstruction.IndexOf("<agent_memory>", StringComparison.Ordinal));
        Assert.IsTrue(systemInstruction.IndexOf("<agent_memory>", StringComparison.Ordinal)
                      < systemInstruction.IndexOf("<conversation_summary>", StringComparison.Ordinal));
        var request = new AgentModelRequest(
            "model",
            systemInstruction,
            [AgentMessage.User("hello")],
            [],
            false,
            100);

        foreach (var item in new (IAiProtocolAdapter Adapter, AiProtocol Protocol, string Property)[]
                 {
                     (new OpenAiChatCompletionsAdapter(), AiProtocol.OpenAiChatCompletions, "messages"),
                     (new OpenAiResponsesAdapter(), AiProtocol.OpenAiResponses, "instructions"),
                     (new AnthropicMessagesAdapter(), AiProtocol.AnthropicMessages, "system"),
                 })
        {
            using var message = item.Adapter.CreateRequest(request, CreateProfile(item.Protocol), null);
            using var document = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());
            var root = document.RootElement;
            var system = item.Property == "messages"
                ? root.GetProperty("messages")[0].GetProperty("content").GetString()
                : root.GetProperty(item.Property).GetString();
            StringAssert.Contains(system!, "current_local_date: 2026-10-19", item.Protocol.ToString());
        }
    }

    private static AgentToolInvocationContext CreateContext() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        EmptyServiceProvider.Instance);

    private static AiConnectionProfile CreateProfile(
        AiProtocol protocol = AiProtocol.OpenAiChatCompletions) => new()
        {
            Id = "runtime-context-test",
            DisplayName = "Runtime Context Test",
            Protocol = protocol,
            BaseUri = new Uri("https://example.test/v1/"),
            Model = "test-model",
            Authentication = new AiAuthenticationConfiguration { Kind = AiAuthenticationKind.None },
            Proxy = new AiProxyConfiguration { Mode = AiProxyMode.Direct },
        };

    private sealed class MutableTimeProvider(DateTimeOffset utcNow, TimeZoneInfo localTimeZone) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override TimeZoneInfo LocalTimeZone { get; } = localTimeZone;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }

    private sealed class CapturingGateway : IAgentModelGateway
    {
        public List<AgentModelRequest> Requests { get; } = [];

        public ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new AgentModelResponse("done", [], "stop", null));
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
            yield return new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: "done");
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseCompleted, FinishReason: "stop");
            await Task.Yield();
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
