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
        Assert.IsTrue(progress.Events.Any(item =>
            item.Kind == AgentRunEventKind.ModelRequestStarted
            && item.Text?.Contains("工具结果已回传", StringComparison.Ordinal) == true));
        Assert.AreEqual(2, gateway.Requests.Count);
        Assert.AreEqual(1, gateway.Requests[1].Messages.Count(item => item.Role == AgentMessageRole.Tool));
    }

    [TestMethod]
    public async Task ToolLoopPublishesAndPreservesReasoningAcrossContinuation()
    {
        var gateway = new SequencedGateway(
            ToolCallStream("call_1", "echo_tool", "{\"value\":\"one\"}", "先读取数据"),
            TextStream("final answer", "根据工具结果整理"));
        var registry = new AgentToolRegistry();
        registry.TryRegister(new RecordingTool());
        var session = CreateSession(gateway);
        var progress = new EventCollector();

        var result = await session.RunAsync(
            "question",
            CreateProfile(),
            registry.CreateSnapshot(),
            progress: progress);

        Assert.AreEqual(AgentSessionStatus.Completed, result.Status);
        CollectionAssert.AreEqual(
            new[] { "先读取数据", "根据工具结果整理" },
            progress.Events
                .Where(item => item.Kind == AgentRunEventKind.ReasoningDelta && item.Text is not null)
                .Select(item => item.Text!)
                .ToArray());
        Assert.AreEqual(
            "先读取数据",
            gateway.Requests[1].Messages.Single(item =>
                item.Role == AgentMessageRole.Assistant && item.ToolCalls.Count > 0).ReasoningText);
        Assert.AreEqual("根据工具结果整理", session.Messages[^1].ReasoningText);
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
    public async Task ModelWrappedCancellationReturnsCancelledAndNextRunCompletes()
    {
        var gateway = new WrappedCancellationGateway();
        var session = CreateSession(gateway);
        var registry = new AgentToolRegistry();

        var firstRun = session.RunAsync("first", CreateProfile(), registry.CreateSnapshot());
        await gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(session.Cancel());

        var cancelled = await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = await session.RunAsync("second", CreateProfile(), registry.CreateSnapshot());

        Assert.AreEqual(AgentSessionStatus.Cancelled, cancelled.Status);
        Assert.AreEqual(AgentSessionStatus.Completed, completed.Status);
        Assert.AreEqual("second answer", completed.FinalText);
        Assert.AreEqual(2, session.Messages.Count);
    }

    [TestMethod]
    public async Task ForceCancelAbortsConnectionAndAllowsNextRun()
    {
        var gateway = new ForceAbortGateway();
        var session = CreateSession(gateway);
        var registry = new AgentToolRegistry();

        var firstRun = session.RunAsync("first", CreateProfile(), registry.CreateSnapshot());
        await gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(session.Cancel());
        Assert.IsTrue(await session.ForceCancelAsync());

        var cancelled = await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = await session.RunAsync("second", CreateProfile(), registry.CreateSnapshot());

        Assert.AreEqual(1, gateway.AbortCount);
        Assert.AreEqual(AgentSessionStatus.Cancelled, cancelled.Status);
        Assert.AreEqual(AgentSessionStatus.Completed, completed.Status);
        Assert.AreEqual("recovered", completed.FinalText);
    }

    [TestMethod]
    public async Task ForceCancelDetachesUncooperativeRunAndIgnoresItsLateResult()
    {
        var gateway = new UncooperativeAbortGateway();
        var session = CreateSession(gateway);
        var registry = new AgentToolRegistry();

        var firstRun = session.RunAsync("first", CreateProfile(), registry.CreateSnapshot());
        await gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(session.Cancel());

        var forceResult = await session.ForceCancelAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        var completed = await session.RunAsync("second", CreateProfile(), registry.CreateSnapshot())
            .WaitAsync(TimeSpan.FromSeconds(5));
        gateway.ReleaseFirst.TrySetResult();
        var cancelled = await firstRun.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(forceResult);
        await gateway.AbortStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(AgentSessionStatus.Completed, completed.Status);
        Assert.AreEqual("new answer", completed.FinalText);
        Assert.AreEqual(AgentSessionStatus.Cancelled, cancelled.Status);
        Assert.AreEqual(AgentSessionStatus.Completed, session.Status);
        Assert.AreEqual(2, session.Messages.Count);
        Assert.AreEqual("second", session.Messages[0].Text);
        Assert.AreEqual("new answer", session.Messages[1].Text);
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
    public async Task AgentFallsBackToNonStreamingWhenStreamingToolsAreUnsupported()
    {
        var gateway = new NonStreamingGateway();
        var registry = new AgentToolRegistry();
        registry.TryRegister(new RecordingTool());
        var session = CreateSession(gateway);

        var result = await session.RunAsync(
            "question",
            CreateProfile(),
            registry.CreateSnapshot(),
            new AgentRunOptions(
                SupportsStreaming: true,
                SupportsTools: true,
                SupportsStreamingTools: false));

        Assert.AreEqual(AgentSessionStatus.Completed, result.Status);
        Assert.AreEqual(1, gateway.SendCount);
        Assert.AreEqual(0, gateway.StreamCount);
        Assert.IsFalse(gateway.Requests.Single().Stream);
        Assert.AreEqual(1, gateway.Requests.Single().Tools.Count);
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

    private static IReadOnlyList<AgentStreamEvent> ToolCallStream(
        string id,
        string name,
        string arguments,
        string? reasoning = null)
    {
        var events = new List<AgentStreamEvent>
        {
            new(AgentStreamEventKind.ResponseStarted),
        };
        if (reasoning is not null)
            events.Add(new AgentStreamEvent(AgentStreamEventKind.ReasoningDelta, Text: reasoning));
        events.AddRange(
        [
            new(AgentStreamEventKind.ToolCallStarted, ToolCallId: id, ToolName: name, ToolIndex: 0),
            new(AgentStreamEventKind.ToolArgumentsDelta, Text: arguments, ToolCallId: id, ToolName: name, ToolIndex: 0),
            new(AgentStreamEventKind.ToolCallCompleted, ToolCallId: id, ToolName: name, ToolIndex: 0),
            new(AgentStreamEventKind.ResponseCompleted, FinishReason: "tool_calls"),
        ]);
        return events;
    }

    private static IReadOnlyList<AgentStreamEvent> TextStream(string text, string? reasoning = null)
    {
        var events = new List<AgentStreamEvent>
        {
            new(AgentStreamEventKind.ResponseStarted),
        };
        if (reasoning is not null)
            events.Add(new AgentStreamEvent(AgentStreamEventKind.ReasoningDelta, Text: reasoning));
        events.Add(new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: text));
        events.Add(new AgentStreamEvent(AgentStreamEventKind.ResponseCompleted, FinishReason: "stop"));
        return events;
    }

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

    private sealed class WrappedCancellationGateway : IAgentModelGateway
    {
        private int _runCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _runCount) == 1)
            {
                Started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException exception)
                {
                    throw new AiModelException(
                        AiModelErrorCategory.Cancelled,
                        "request_cancelled",
                        "模型请求已取消。",
                        innerException: exception);
                }
                yield break;
            }
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
            yield return new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: "second answer");
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseCompleted, FinishReason: "stop");
        }
    }

    private sealed class ForceAbortGateway : IAgentModelGateway, IAgentModelRequestAborter
    {
        private readonly TaskCompletionSource _aborted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AbortCount { get; private set; }

        public ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _runCount) == 1)
            {
                Started.TrySetResult();
                await _aborted.Task;
                throw new OperationCanceledException(cancellationToken);
            }
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
            yield return new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: "recovered");
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseCompleted, FinishReason: "stop");
        }

        public ValueTask AbortConnectionAsync(
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default)
        {
            AbortCount++;
            _aborted.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UncooperativeAbortGateway : IAgentModelGateway, IAgentModelRequestAborter
    {
        private readonly TaskCompletionSource _neverFinishAbort = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AbortStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _runCount) == 1)
            {
                Started.TrySetResult();
                yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
                await ReleaseFirst.Task;
                yield return new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: "stale answer");
                yield return new AgentStreamEvent(AgentStreamEventKind.ResponseCompleted, FinishReason: "stop");
                yield break;
            }
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
            yield return new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: "new answer");
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseCompleted, FinishReason: "stop");
        }

        public ValueTask AbortConnectionAsync(
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default)
        {
            AbortStarted.TrySetResult();
            return new ValueTask(_neverFinishAbort.Task);
        }
    }

    private sealed class NonStreamingGateway : IAgentModelGateway
    {
        public int SendCount { get; private set; }

        public int StreamCount { get; private set; }

        public List<AgentModelRequest> Requests { get; } = [];

        public ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            Requests.Add(request);
            return ValueTask.FromResult(new AgentModelResponse("done", [], "stop", null));
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamCount++;
            await Task.CompletedTask;
            yield break;
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
