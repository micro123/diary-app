using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Protocols;
using Diary.Agent.Runtime;
using Diary.Agent.Tools;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.AgentTests;

[TestClass]
public sealed class AgentSessionServiceTests
{
    [TestMethod]
    public async Task ToolLoopExecutesSeriallyAndCommitsOnlyCompletedHistory()
    {
        var gateway = new SequencedGateway(
            ToolCallStream("call_1", "echo_tool", "{\"value\":\"one\"}"),
            TextStream("final answer"));
        var registry = new AgentToolRegistry();
        var tool = new RecordingTool();
        registry.TryRegister(tool);
        var session = CreateSession(gateway);
        var progress = new EventCollector();

        var result = await session.RunAsync(
            "question",
            CreateProfile(),
            registry.CreateSnapshot(),
            progress: progress);

        Assert.AreEqual(AgentSessionStatus.Completed, result.Status);
        Assert.AreEqual("final answer", result.FinalText);
        Assert.AreEqual(2, result.Rounds);
        Assert.AreEqual(1, result.ToolCalls);
        Assert.AreEqual(1, tool.InvocationCount);
        Assert.AreEqual(4, session.Messages.Count);
        Assert.AreEqual(AgentMessageRole.User, session.Messages[0].Role);
        Assert.AreEqual(AgentMessageRole.Assistant, session.Messages[1].Role);
        Assert.AreEqual(AgentMessageRole.Tool, session.Messages[2].Role);
        Assert.AreEqual(AgentMessageRole.Assistant, session.Messages[3].Role);
        Assert.AreEqual("final answer", session.Messages[3].Text);
        Assert.IsTrue(progress.Events.Any(item => item.Kind == AgentRunEventKind.ToolCompleted));
        Assert.AreEqual(2, gateway.Requests.Count);
        Assert.AreEqual(1, gateway.Requests[1].Messages.Count(item => item.Role == AgentMessageRole.Tool));
    }

    [TestMethod]
    public async Task IncompleteToolStreamDoesNotExecuteOrCommitHistory()
    {
        var gateway = new SequencedGateway(IncompleteToolCallStream());
        var registry = new AgentToolRegistry();
        var tool = new RecordingTool();
        registry.TryRegister(tool);
        var session = CreateSession(gateway);

        var result = await session.RunAsync("question", CreateProfile(), registry.CreateSnapshot());

        Assert.AreEqual(AgentSessionStatus.Failed, result.Status);
        Assert.AreEqual("stream_incomplete", result.ErrorCode);
        Assert.AreEqual(0, tool.InvocationCount);
        Assert.AreEqual(0, session.Messages.Count);
    }

    [TestMethod]
    public async Task CancellationDoesNotCommitPartialAssistantText()
    {
        var gateway = new BlockingGateway();
        var session = CreateSession(gateway);
        var registry = new AgentToolRegistry();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await session.RunAsync(
            "question",
            CreateProfile(),
            registry.CreateSnapshot(),
            cancellationToken: cancellation.Token);

        Assert.AreEqual(AgentSessionStatus.Cancelled, result.Status);
        Assert.AreEqual(0, session.Messages.Count);
    }

    [TestMethod]
    public async Task ChatOnlyConnectionReceivesNoToolsAndCompletesNormally()
    {
        var gateway = new SequencedGateway(TextStream("chat response"));
        var registry = new AgentToolRegistry();
        registry.TryRegister(new RecordingTool());
        var session = CreateSession(gateway);

        var result = await session.RunAsync(
            "hello",
            CreateProfile(),
            registry.CreateSnapshot(),
            new AgentRunOptions(SupportsStreaming: true, SupportsTools: false));

        Assert.AreEqual(AgentSessionStatus.Completed, result.Status);
        Assert.AreEqual(0, gateway.Requests.Single().Tools.Count);
    }

    [TestMethod]
    public async Task ToolBudgetStopsBeforeExecutingExcessCalls()
    {
        var gateway = new SequencedGateway(TwoToolCallsStream());
        var registry = new AgentToolRegistry();
        var tool = new RecordingTool();
        registry.TryRegister(tool);
        var session = CreateSession(gateway);

        var result = await session.RunAsync(
            "question",
            CreateProfile(),
            registry.CreateSnapshot(),
            new AgentRunOptions(Budget: new AgentRunBudget(MaxRounds: 2, MaxToolCalls: 1)));

        Assert.AreEqual(AgentSessionStatus.Failed, result.Status);
        Assert.AreEqual("tool_budget_exceeded", result.ErrorCode);
        Assert.AreEqual(0, tool.InvocationCount);
        Assert.AreEqual(0, session.Messages.Count);
    }

    [TestMethod]
    public async Task SameSessionRejectsConcurrentRuns()
    {
        var gateway = new BlockingGateway();
        var session = CreateSession(gateway);
        var registry = new AgentToolRegistry();
        using var firstCancellation = new CancellationTokenSource();
        var first = session.RunAsync(
            "first",
            CreateProfile(),
            registry.CreateSnapshot(),
            cancellationToken: firstCancellation.Token);
        await gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await session.RunAsync("second", CreateProfile(), registry.CreateSnapshot()));
        firstCancellation.Cancel();
        await first;
    }

    [TestMethod]
    public async Task LocalNoteMarkerNeverReachesActualModelRequestBody()
    {
        const string localNoteMarker = "LOCAL_NOTE_MODEL_BODY_GUARD_4A8D6B";
        var gateway = new SequencedGateway(
            ToolCallStream("call_note_guard", "diary_query_work_items", "{}"),
            TextStream("done"));
        var item = new ScriptWorkItem(
            42,
            "2026-10-08",
            "可发送的事项标题",
            1.25,
            2,
            localNoteMarker,
            ImmutableArray<ScriptWorkTag>.Empty);
        var registry = new AgentToolRegistry();
        Assert.IsTrue(registry.TryRegister(new QueryWorkItemsTool(new FixedWorkItemQueryApi(item))));
        var session = CreateSession(gateway);

        var result = await session.RunAsync(
            "查询今天的事项",
            CreateProfile(),
            registry.CreateSnapshot());

        Assert.AreEqual(AgentSessionStatus.Completed, result.Status);
        Assert.AreEqual(2, gateway.Requests.Count);
        using var request = new OpenAiChatCompletionsAdapter().CreateRequest(
            gateway.Requests[1],
            CreateProfile(),
            credential: null);
        var body = await request.Content!.ReadAsStringAsync();
        Assert.IsFalse(body.Contains(localNoteMarker, StringComparison.Ordinal));
        using var requestDocument = JsonDocument.Parse(body);
        var toolMessage = requestDocument.RootElement.GetProperty("messages")
            .EnumerateArray()
            .Single(message => message.GetProperty("role").GetString() == "tool");
        using var envelopeDocument = JsonDocument.Parse(toolMessage.GetProperty("content").GetString()!);
        using var toolResultDocument = JsonDocument.Parse(
            envelopeDocument.RootElement.GetProperty("Content").GetString()!);
        Assert.AreEqual(
            "可发送的事项标题",
            toolResultDocument.RootElement.GetProperty("items")[0].GetProperty("Title").GetString());
    }

    private static AgentSessionService CreateSession(IAgentModelGateway gateway) => new(
        gateway,
        new AgentToolExecutor(),
        EmptyServiceProvider.Instance);

    private static AiConnectionProfile CreateProfile() => new()
    {
        Id = "test",
        DisplayName = "Test",
        Protocol = AiProtocol.OpenAiChatCompletions,
        BaseUri = new Uri("https://example.test/v1/"),
        Model = "test-model",
        Authentication = new AiAuthenticationConfiguration { Kind = AiAuthenticationKind.None },
        Proxy = new AiProxyConfiguration { Mode = AiProxyMode.Direct },
    };

    private static IReadOnlyList<AgentStreamEvent> ToolCallStream(string id, string name, string arguments) =>
    [
        new(AgentStreamEventKind.ResponseStarted),
        new(AgentStreamEventKind.ToolCallStarted, ToolCallId: id, ToolName: name, ToolIndex: 0),
        new(AgentStreamEventKind.ToolArgumentsDelta, Text: arguments, ToolCallId: id, ToolName: name, ToolIndex: 0),
        new(AgentStreamEventKind.ToolCallCompleted, ToolCallId: id, ToolName: name, ToolIndex: 0),
        new(AgentStreamEventKind.ResponseCompleted, FinishReason: "tool_calls"),
    ];

    private static IReadOnlyList<AgentStreamEvent> TextStream(string text) =>
    [
        new(AgentStreamEventKind.ResponseStarted),
        new(AgentStreamEventKind.TextDelta, Text: text),
        new(AgentStreamEventKind.ResponseCompleted, FinishReason: "stop"),
    ];

    private static IReadOnlyList<AgentStreamEvent> IncompleteToolCallStream() =>
    [
        new(AgentStreamEventKind.ResponseStarted),
        new(AgentStreamEventKind.ToolCallStarted, ToolCallId: "call_1", ToolName: "echo_tool", ToolIndex: 0),
        new(AgentStreamEventKind.ToolArgumentsDelta, Text: "{\"value\":", ToolCallId: "call_1", ToolName: "echo_tool", ToolIndex: 0),
    ];

    private static IReadOnlyList<AgentStreamEvent> TwoToolCallsStream() =>
    [
        .. ToolCallStream("call_1", "echo_tool", "{}") .SkipLast(1),
        new(AgentStreamEventKind.ToolCallStarted, ToolCallId: "call_2", ToolName: "echo_tool", ToolIndex: 1),
        new(AgentStreamEventKind.ToolArgumentsDelta, Text: "{}", ToolCallId: "call_2", ToolName: "echo_tool", ToolIndex: 1),
        new(AgentStreamEventKind.ToolCallCompleted, ToolCallId: "call_2", ToolName: "echo_tool", ToolIndex: 1),
        new(AgentStreamEventKind.ResponseCompleted, FinishReason: "tool_calls"),
    ];

    private sealed class SequencedGateway(params IReadOnlyList<AgentStreamEvent>[] responses) : IAgentModelGateway
    {
        private int _index;

        public List<AgentModelRequest> Requests { get; } = [];

        public ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var response = responses[_index++];
            foreach (var item in response)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class BlockingGateway : IAgentModelGateway
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
            yield return new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: "partial");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class RecordingTool : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        public int InvocationCount { get; private set; }

        public AgentToolDescriptor Descriptor { get; } = new(
            "echo",
            "echo_tool",
            "Echo",
            "Echo",
            Schema,
            AgentToolOrigin.BuiltIn,
            AgentToolRisk.ReadOnly,
            "tests");

        public ValueTask<AgentToolResult> InvokeAsync(
            JsonElement arguments,
            AgentToolInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return ValueTask.FromResult(AgentToolResult.Success(arguments.GetRawText()));
        }
    }

    private sealed class FixedWorkItemQueryApi(ScriptWorkItem item) : IWorkItemQueryScriptApi
    {
        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(
            ScriptWorkItemQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptWorkItemQueryResult.Success([item], query));
    }

    private sealed class EventCollector : IProgress<AgentRunEvent>
    {
        public List<AgentRunEvent> Events { get; } = [];

        public void Report(AgentRunEvent value) => Events.Add(value);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
