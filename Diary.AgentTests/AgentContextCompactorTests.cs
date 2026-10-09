using System.Runtime.CompilerServices;
using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Protocols;
using Diary.Agent.Runtime;
using Diary.Agent.Tools;

namespace Diary.AgentTests;

[TestClass]
public sealed class AgentContextCompactorTests
{
    [TestMethod]
    public async Task AutomaticCompactionSummarizesOldTurnsAndKeepsRecentTurns()
    {
        var gateway = new CompactionGateway("结构化历史摘要");
        var compactor = new AgentContextCompactor(gateway);
        var history = CreateLongHistory();

        var result = await compactor.CompactIfNeededAsync(
            null,
            history,
            "新的用户问题",
            [],
            CreateProfile(),
            maxOutputTokens: 512);

        Assert.IsTrue(result.Compacted);
        Assert.AreEqual("结构化历史摘要", result.Summary);
        Assert.AreEqual(4, result.CompactedMessageCount);
        Assert.AreEqual(4, result.RetainedMessages.Count);
        Assert.AreEqual("第二轮问题" + LongText, result.RetainedMessages[0].Text);
        Assert.IsFalse(result.UsedFallback);
        Assert.AreEqual(1, gateway.SummaryRequests.Count);
        StringAssert.Contains(gateway.SummaryRequests[0].SystemInstruction, "压缩 DiaryApp AI 助手");
    }

    [TestMethod]
    public async Task SummaryProviderFailureUsesDeterministicLocalFallback()
    {
        var gateway = new CompactionGateway("unused") { FailSummary = true };
        var compactor = new AgentContextCompactor(gateway);

        var result = await compactor.CompactIfNeededAsync(
            "既有摘要",
            CreateLongHistory(),
            "新的用户问题",
            [],
            CreateProfile(),
            maxOutputTokens: 512);

        Assert.IsTrue(result.Compacted);
        Assert.IsTrue(result.UsedFallback);
        StringAssert.Contains(result.Summary!, "既有摘要");
        StringAssert.Contains(result.Summary!, "第一轮问题");
    }

    [TestMethod]
    public async Task SessionInjectsSummaryReportsEventAndCommitsCompressedHistory()
    {
        var gateway = new CompactionGateway("会话压缩摘要", "最终回答");
        var session = new AgentSessionService(
            gateway,
            new AgentToolExecutor(),
            EmptyServiceProvider.Instance,
            contextCompactor: new AgentContextCompactor(gateway));
        session.RestoreSession(CreatePersistableLongHistory());
        var progress = new EventCollector();

        var result = await session.RunAsync(
            "继续处理",
            CreateProfile(),
            new AgentToolRegistry().CreateSnapshot(),
            new AgentRunOptions(
                SupportsStreaming: true,
                SupportsTools: false,
                Budget: new AgentRunBudget(MaxOutputTokens: 512)),
            progress);

        Assert.AreEqual(AgentSessionStatus.Completed, result.Status);
        Assert.AreEqual(1, session.ContextCompactionCount);
        Assert.AreEqual("会话压缩摘要", session.ContextSummary);
        Assert.AreEqual(6, session.Messages.Count);
        Assert.IsTrue(progress.Events.Any(item => item.Kind == AgentRunEventKind.ContextCompacted));
        Assert.AreEqual(1, gateway.MainRequests.Count);
        StringAssert.Contains(gateway.MainRequests[0].SystemInstruction, "会话压缩摘要");
        Assert.IsFalse(gateway.MainRequests[0].Messages.Any(message => message.Text.StartsWith("第一轮", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ResponsesProtocolStateTriggersCompactionAndIsClearedBeforeMainRequest()
    {
        var outputItems = new OpenAiResponsesProtocolState(
        [
            JsonSerializer.SerializeToElement(new
            {
                type = "message",
                content = new string('s', 12_000),
            }),
        ]);
        var gateway = new CompactionGateway("协议状态压缩摘要")
        {
            MainProtocolState = outputItems,
        };
        var session = new AgentSessionService(
            gateway,
            new AgentToolExecutor(),
            EmptyServiceProvider.Instance,
            contextCompactor: new AgentContextCompactor(gateway));
        var profile = CreateProfile(AiProtocol.OpenAiResponses);
        var options = new AgentRunOptions(
            SupportsStreaming: true,
            SupportsTools: false,
            Budget: new AgentRunBudget(MaxOutputTokens: 512));
        var snapshot = new AgentToolRegistry().CreateSnapshot();

        await session.RunAsync("第一轮", profile, snapshot, options);
        await session.RunAsync("第二轮", profile, snapshot, options);
        await session.RunAsync("第三轮", profile, snapshot, options);
        await session.RunAsync("第四轮", profile, snapshot, options);

        Assert.AreEqual(1, session.ContextCompactionCount);
        Assert.AreEqual(1, gateway.SummaryRequests.Count);
        Assert.AreEqual(4, gateway.MainRequests.Count);
        Assert.IsNull(gateway.MainRequests[^1].ProtocolState);
        StringAssert.Contains(gateway.MainRequests[^1].SystemInstruction, "协议状态压缩摘要");
    }

    private static readonly string LongText = "-" + new string('x', 1800);

    private static IReadOnlyList<AgentMessage> CreateLongHistory() =>
    [
        AgentMessage.User("第一轮问题" + LongText),
        AgentMessage.Assistant("第一轮回答" + LongText,
        [
            new AgentToolCall(
                "call-old",
                "old_tool",
                JsonSerializer.SerializeToElement(new { value = "old" })),
        ]),
        AgentMessage.Tool("call-old", "第一轮工具结果" + LongText),
        AgentMessage.Assistant("第一轮最终回答" + LongText),
        AgentMessage.User("第二轮问题" + LongText),
        AgentMessage.Assistant("第二轮回答" + LongText),
        AgentMessage.User("第三轮问题" + LongText),
        AgentMessage.Assistant("第三轮回答" + LongText),
    ];

    private static IReadOnlyList<AgentMessage> CreatePersistableLongHistory() =>
    [
        AgentMessage.User("第一轮问题" + LongText),
        AgentMessage.Assistant("第一轮回答" + LongText),
        AgentMessage.User("第二轮问题" + LongText),
        AgentMessage.Assistant("第二轮回答" + LongText),
        AgentMessage.User("第三轮问题" + LongText),
        AgentMessage.Assistant("第三轮回答" + LongText),
        AgentMessage.User("第四轮问题" + LongText),
        AgentMessage.Assistant("第四轮回答" + LongText),
    ];

    private static AiConnectionProfile CreateProfile(
        AiProtocol protocol = AiProtocol.OpenAiChatCompletions) => new()
        {
            Id = "context-test",
            DisplayName = "Context Test",
            Protocol = protocol,
            BaseUri = new Uri("https://example.test/v1/"),
            Model = "test-model",
            ContextWindowTokens = 8192,
            AutomaticContextCompression = true,
            ContextCompressionThresholdPercent = 50,
            Authentication = new AiAuthenticationConfiguration { Kind = AiAuthenticationKind.None },
            Proxy = new AiProxyConfiguration { Mode = AiProxyMode.Direct },
        };

    private sealed class CompactionGateway(string summary, string mainResponse = "done") : IAgentModelGateway
    {
        public bool FailSummary { get; init; }

        public AgentProtocolState? MainProtocolState { get; init; }

        public List<AgentModelRequest> SummaryRequests { get; } = [];

        public List<AgentModelRequest> MainRequests { get; } = [];

        public ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default)
        {
            SummaryRequests.Add(request);
            if (FailSummary)
                throw new InvalidOperationException("summary failed");
            return ValueTask.FromResult(new AgentModelResponse(
                summary,
                [],
                "stop",
                new AgentUsage(100, 20, 120)));
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            MainRequests.Add(request);
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
            yield return new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: mainResponse);
            yield return new AgentStreamEvent(
                AgentStreamEventKind.UsageUpdated,
                Usage: new AgentUsage(200, 30, 230));
            yield return new AgentStreamEvent(
                AgentStreamEventKind.ResponseCompleted,
                FinishReason: "stop",
                ProtocolState: MainProtocolState);
            await Task.Yield();
        }
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
