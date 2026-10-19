using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Protocols;
using Diary.Agent.Runtime;
using Diary.Agent.Tools;

namespace Diary.AgentTests;

[TestClass]
public sealed class AgentMemoryTests
{
    [TestMethod]
    public async Task StoreSavesUtf8CreatesBackupAndRestoresPreviousVersion()
    {
        var path = CreateMemoryPath();
        var store = new AgentMemoryStore(path);

        var first = await store.SaveAsync("# 偏好\n\n- 使用中文。\n");
        Assert.IsFalse(first.HasBackup);
        var second = await store.SaveAsync("# 偏好\n\n- 使用中文。\n- 日期使用 yyyy-MM-dd。\n", first.Version);
        Assert.IsTrue(second.HasBackup);
        Assert.AreEqual("# 偏好\n\n- 使用中文。\n", await File.ReadAllTextAsync(path + ".bak", Encoding.UTF8));

        var restored = await store.RestoreBackupAsync(second.Version);
        Assert.AreEqual("# 偏好\n\n- 使用中文。\n", restored.Content);
        Assert.AreEqual("# 偏好\n\n- 使用中文。\n- 日期使用 yyyy-MM-dd。\n", await File.ReadAllTextAsync(path + ".bak", Encoding.UTF8));
    }

    [TestMethod]
    public async Task StoreRejectsOversizedInvalidUtf8AndStaleVersion()
    {
        var path = CreateMemoryPath();
        var store = new AgentMemoryStore(path);
        var initial = await store.SaveAsync("initial");

        var oversized = new string('x', AgentMemoryStore.MaxBytes + 1);
        var sizeError = await Assert.ThrowsExactlyAsync<AgentMemoryException>(async () =>
            await store.SaveAsync(oversized, initial.Version));
        Assert.AreEqual("memory_too_large", sizeError.Code);

        await File.WriteAllTextAsync(path, "externally changed", Encoding.UTF8);
        var conflict = await Assert.ThrowsExactlyAsync<AgentMemoryException>(async () =>
            await store.SaveAsync("replacement", initial.Version));
        Assert.AreEqual("memory_conflict", conflict.Code);

        await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0xfd]);
        var encodingError = await Assert.ThrowsExactlyAsync<AgentMemoryException>(async () =>
            await store.LoadAsync());
        Assert.AreEqual("memory_invalid_utf8", encodingError.Code);

        var invalidText = await Assert.ThrowsExactlyAsync<AgentMemoryException>(async () =>
            await store.SaveAsync("\ud800"));
        Assert.AreEqual("memory_invalid_utf8", invalidText.Code);
    }

    [TestMethod]
    public async Task MissingAndEmptyFilesHaveDifferentVersions()
    {
        var path = CreateMemoryPath();
        var store = new AgentMemoryStore(path);
        var missing = await store.LoadAsync();
        await File.WriteAllTextAsync(path, string.Empty, Encoding.UTF8);

        var conflict = await Assert.ThrowsExactlyAsync<AgentMemoryException>(async () =>
            await store.SaveAsync("new content", missing.Version));

        Assert.AreEqual("memory_conflict", conflict.Code);
    }

    [TestMethod]
    public async Task MemoryToolRejectDoesNotWriteAndConfirmUsesVersionCheck()
    {
        var path = CreateMemoryPath();
        var store = new AgentMemoryStore(path);
        await store.SaveAsync("old memory");
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new AgentMemoryTool(store, confirmations);

        var rejectedRequest = WaitForRequest(confirmations);
        var rejectedInvocation = tool.InvokeAsync(
            Arguments("new memory", "用户要求记住新的工作习惯"),
            CreateContext()).AsTask();
        var rejected = await rejectedRequest;
        StringAssert.Contains(rejected.PreviewText!, "old memory");
        StringAssert.Contains(rejected.PreviewText!, "new memory");
        confirmations.CompleteExternal(rejected.ConfirmationId, AgentConfirmationDecision.Reject);
        var rejectedResult = await rejectedInvocation;
        Assert.AreEqual("user_rejected", rejectedResult.ErrorCode);
        Assert.AreEqual("old memory", (await store.LoadAsync()).Content);

        var confirmedRequest = WaitForRequest(confirmations);
        var confirmedInvocation = tool.InvokeAsync(
            Arguments("confirmed memory", "用户明确要求长期记住"),
            CreateContext()).AsTask();
        var confirmed = await confirmedRequest;
        await File.WriteAllTextAsync(path, "manual edit", Encoding.UTF8);
        confirmations.CompleteExternal(confirmed.ConfirmationId, AgentConfirmationDecision.Confirm);
        var conflictResult = await confirmedInvocation;
        Assert.AreEqual("memory_conflict", conflictResult.ErrorCode);
        Assert.AreEqual("manual edit", (await store.LoadAsync()).Content);
    }

    [TestMethod]
    public async Task SessionInjectsMemoryWithoutPersistingItAsConversationMessage()
    {
        const string memoryMarker = "MEMORY_ONLY_MARKER";
        var store = new AgentMemoryStore(CreateMemoryPath());
        await store.SaveAsync($"# 偏好\n\n- {memoryMarker}\n");
        var gateway = new CapturingGateway();
        var session = new AgentSessionService(
            gateway,
            new AgentToolExecutor(),
            EmptyServiceProvider.Instance,
            memoryStore: store);

        var result = await session.RunAsync(
            "hello",
            CreateProfile(),
            new AgentToolRegistry().CreateSnapshot(),
            new AgentRunOptions(SupportsTools: false, UseMemory: true));

        Assert.AreEqual(AgentSessionStatus.Completed, result.Status);
        Assert.AreEqual(1, gateway.Requests.Count);
        StringAssert.Contains(gateway.Requests[0].SystemInstruction, memoryMarker);
        Assert.IsFalse(session.Messages.Any(message => message.Text.Contains(memoryMarker, StringComparison.Ordinal)));
        Assert.IsFalse(AgentConversationProjection.CreatePersistedMessages(session.Messages)
            .Any(message => message.Content.Contains(memoryMarker, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AutomaticCompactionKeepsMemoryInRebuiltSystemInstruction()
    {
        const string memoryMarker = "MEMORY_AFTER_COMPACTION";
        var store = new AgentMemoryStore(CreateMemoryPath());
        await store.SaveAsync(memoryMarker);
        var gateway = new CapturingGateway();
        var session = new AgentSessionService(
            gateway,
            new AgentToolExecutor(),
            EmptyServiceProvider.Instance,
            memoryStore: store);
        var longText = new string('x', 2500);
        session.RestoreSession(
        [
            AgentMessage.User("one" + longText),
            AgentMessage.Assistant("one answer" + longText),
            AgentMessage.User("two" + longText),
            AgentMessage.Assistant("two answer" + longText),
            AgentMessage.User("three" + longText),
            AgentMessage.Assistant("three answer" + longText),
            AgentMessage.User("four" + longText),
            AgentMessage.Assistant("four answer" + longText),
        ]);
        var profile = CreateProfile() with
        {
            ContextWindowTokens = 4096,
            AutomaticContextCompression = true,
            ContextCompressionThresholdPercent = 50,
        };

        var result = await session.RunAsync(
            "continue",
            profile,
            new AgentToolRegistry().CreateSnapshot(),
            new AgentRunOptions(SupportsTools: false, UseMemory: true));

        Assert.AreEqual(AgentSessionStatus.Completed, result.Status);
        Assert.AreEqual(1, session.ContextCompactionCount);
        StringAssert.Contains(gateway.Requests[^1].SystemInstruction, memoryMarker);
        StringAssert.Contains(gateway.Requests[^1].SystemInstruction, "conversation_summary");
    }

    [TestMethod]
    public async Task AllProtocolsPutAgentMemoryInSystemField()
    {
        const string memoryMarker = "memory-protocol-marker";
        var request = new AgentModelRequest(
            "model",
            AgentContextCompactor.BuildSystemInstruction(null, memoryMarker),
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
            StringAssert.Contains(system!, memoryMarker, item.Protocol.ToString());
        }
    }

    [TestMethod]
    public void MemoryToolArgumentsAreHiddenFromPersistedToolSummary()
    {
        var registry = new AgentToolRegistry();
        registry.TryRegister(new AgentMemoryTool(
            new AgentMemoryStore(CreateMemoryPath()),
            new AgentConfirmationCoordinator()));
        var arguments = Arguments("PRIVATE_MEMORY_MARKER", "PRIVATE_REASON_MARKER");

        var summary = AgentSessionService.CreateToolArgumentsSummary(
            registry.CreateSnapshot(),
            "diary_update_agent_memory",
            arguments);

        Assert.AreEqual("{\"parameters\":\"[已隐藏]\"}", summary);
        Assert.IsFalse(summary.Contains("PRIVATE_MEMORY_MARKER", StringComparison.Ordinal));
        Assert.IsFalse(summary.Contains("PRIVATE_REASON_MARKER", StringComparison.Ordinal));
    }

    private static JsonElement Arguments(string content, string reason) =>
        JsonSerializer.SerializeToElement(new { content, reason });

    private static Task<ExternalToolConfirmationRequest> WaitForRequest(AgentConfirmationCoordinator coordinator)
    {
        var completion = new TaskCompletionSource<ExternalToolConfirmationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ExternalConfirmationRequested += (_, request) => completion.TrySetResult(request);
        return completion.Task;
    }

    private static AgentToolInvocationContext CreateContext() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        EmptyServiceProvider.Instance);

    private static AiConnectionProfile CreateProfile(
        AiProtocol protocol = AiProtocol.OpenAiChatCompletions) => new()
        {
            Id = "memory-test",
            DisplayName = "Memory Test",
            Protocol = protocol,
            BaseUri = new Uri("https://example.test/v1/"),
            Model = "test-model",
            Authentication = new AiAuthenticationConfiguration { Kind = AiAuthenticationKind.None },
            Proxy = new AiProxyConfiguration { Mode = AiProxyMode.Direct },
        };

    private static string CreateMemoryPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiaryAgentMemoryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "memory.md");
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
