using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Protocols;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.AgentTests;

[TestClass]
public sealed class AiConfigurationTests
{
    [TestMethod]
    public void ConnectionStoreRoundTripsReferencesAndCapabilitiesWithoutSecrets()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "settings.json");
        var profile = CreateProfile() with { CheckCertificateRevocation = false };
        var settings = new AiAgentSettings
        {
            DefaultProfileId = profile.Id,
            Profiles = [profile],
            Capabilities = new Dictionary<string, AiConnectionCapabilities>
            {
                [profile.Id] = new(
                    true,
                    true,
                    true,
                    DateTimeOffset.Parse("2026-10-08T00:00:00Z"),
                    SupportsStreamingTools: true,
                    SupportsParallelTools: true,
                    SupportsForcedToolChoice: true),
            },
        };
        var store = new AiConnectionStore(path);

        store.Save(settings);
        var loaded = store.Load();
        var raw = File.ReadAllText(path);

        Assert.AreEqual(AiSettingsLoadStatus.Loaded, loaded.Status);
        Assert.AreEqual(profile.Id, loaded.Settings?.DefaultProfileId);
        Assert.IsTrue(loaded.Settings?.Capabilities[profile.Id].SupportsStreaming);
        Assert.IsTrue(loaded.Settings?.Capabilities[profile.Id].SupportsStreamingTools);
        Assert.IsTrue(loaded.Settings?.Capabilities[profile.Id].SupportsParallelTools);
        Assert.IsTrue(loaded.Settings?.Capabilities[profile.Id].SupportsForcedToolChoice);
        Assert.AreEqual(1_000_000, loaded.Settings?.Profiles.Single().ContextWindowTokens);
        Assert.AreEqual(70, loaded.Settings?.Profiles.Single().ContextCompressionThresholdPercent);
        Assert.IsTrue(loaded.Settings?.Profiles.Single().AutomaticContextCompression);
        Assert.IsFalse(loaded.Settings?.Profiles.Single().CheckCertificateRevocation);
        StringAssert.Contains(raw, "diary.ai/internal/api-key");
        Assert.IsFalse(raw.Contains("actual-secret-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnreadableConnectionStoreIsNotOverwritten()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "settings.json");
        const string corrupt = "{broken-json";
        File.WriteAllText(path, corrupt);
        var store = new AiConnectionStore(path);

        var result = store.Load();
        Assert.AreEqual(AiSettingsLoadStatus.Unreadable, result.Status);
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Save(new AiAgentSettings()));
        Assert.AreEqual(corrupt, File.ReadAllText(path));
    }

    [TestMethod]
    public async Task CredentialStoreSupportsMemoryEnvironmentAndEncryptedPersistentValues()
    {
        var store = new AiCredentialStore();
        var memoryReference = $"memory:test/{Guid.NewGuid():N}";
        await store.SetAsync(memoryReference, "memory-secret".AsMemory());
        Assert.AreEqual("memory-secret", (await store.GetAsync(memoryReference))?.Reveal());
        await store.DeleteAsync(memoryReference);
        Assert.IsFalse(await store.ExistsAsync(memoryReference));

        var environmentName = "DIARY_AGENT_TEST_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        Environment.SetEnvironmentVariable(environmentName, "environment-secret");
        try
        {
            Assert.AreEqual(
                "environment-secret",
                (await store.GetAsync("env:" + environmentName))?.Reveal());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await store.SetAsync("env:" + environmentName, "changed".AsMemory()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
        }

        var persistentReference = $"diary.ai/test-{Guid.NewGuid():N}/api-key";
        const string persistentSecret = "persistent-secret-must-be-encrypted";
        await store.SetAsync(persistentReference, persistentSecret.AsMemory());
        Assert.AreEqual(persistentSecret, (await store.GetAsync(persistentReference))?.Reveal());
        var credentialPath = Path.Combine(FsTools.GetApplicationConfigDirectory(), "ai-agent-credentials.dat");
        var bytes = File.ReadAllBytes(credentialPath);
        CollectionAssert.AreEqual("DiaryGCM"u8.ToArray(), bytes[..8]);
        Assert.IsFalse(Encoding.UTF8.GetString(bytes).Contains(persistentSecret, StringComparison.Ordinal));
        await store.DeleteAsync(persistentReference);
    }

    [TestMethod]
    public void CredentialValueNeverRevealsSecretThroughToString()
    {
        var credential = new CredentialValue("highly-sensitive");
        Assert.AreEqual("********", credential.ToString());
        Assert.AreEqual("highly-sensitive", credential.Reveal());
    }

    [TestMethod]
    public async Task ConnectionManagerDoesNotProbeOnLoadAndTestDoesNotSaveWorkingCopy()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "settings.json");
        var saved = CreateProfile();
        var store = new AiConnectionStore(path);
        store.Save(new AiAgentSettings { DefaultProfileId = saved.Id, Profiles = [saved] });
        var gateway = new ProbeGateway();
        var manager = new AiConnectionManager(store, new AiConnectionProbeService(gateway));
        Assert.AreEqual(0, gateway.SendCount);

        var workingCopy = saved with { DisplayName = "Working Copy" };
        var result = await manager.TestAsync(workingCopy);
        var reloaded = store.Load();

        Assert.IsTrue(result.SupportsChat);
        Assert.IsTrue(result.SupportsStreaming);
        Assert.IsTrue(result.SupportsTools);
        Assert.IsTrue(result.SupportsStreamingTools);
        Assert.IsTrue(result.SupportsParallelTools);
        Assert.IsTrue(result.SupportsForcedToolChoice);
        Assert.AreEqual("Internal", reloaded.Settings?.Profiles.Single().DisplayName);
        Assert.AreEqual("Internal", manager.Settings.Profiles.Single().DisplayName);
    }

    [TestMethod]
    public async Task BasicConnectionTestSendsOneRequestAndReportsProgress()
    {
        var root = CreateTemporaryDirectory();
        var gateway = new ProbeGateway();
        var manager = new AiConnectionManager(
            new AiConnectionStore(Path.Combine(root, "settings.json")),
            new AiConnectionProbeService(gateway));
        var progress = new RecordingProgress<AiConnectionProbeProgress>();

        var result = await manager.TestConnectionAsync(CreateProfile(), progress);

        Assert.IsTrue(result.SupportsChat);
        Assert.AreEqual(AiConnectionProbeService.BasicProbeOnlyCode, result.ErrorCode);
        Assert.AreEqual(1, gateway.SendCount);
        Assert.AreEqual(1, progress.Items.Count);
        Assert.AreEqual(AiConnectionProbeStage.BasicChat, progress.Items[0].Stage);
    }

    [TestMethod]
    public async Task FullCapabilityProbeReportsAllStages()
    {
        var root = CreateTemporaryDirectory();
        var gateway = new ProbeGateway();
        var manager = new AiConnectionManager(
            new AiConnectionStore(Path.Combine(root, "settings.json")),
            new AiConnectionProbeService(gateway));
        var progress = new RecordingProgress<AiConnectionProbeProgress>();

        var result = await manager.TestAsync(CreateProfile(), progress);

        Assert.IsTrue(result.SupportsForcedToolChoice);
        CollectionAssert.AreEqual(
            Enum.GetValues<AiConnectionProbeStage>(),
            progress.Items.Select(item => item.Stage).ToArray());
    }

    [TestMethod]
    public async Task FullProbeTimeoutPreservesCompletedAgentCapabilities()
    {
        var root = CreateTemporaryDirectory();
        var manager = new AiConnectionManager(
            new AiConnectionStore(Path.Combine(root, "settings.json")),
            new AiConnectionProbeService(
                new LateHangingProbeGateway(),
                fullProbeTimeout: TimeSpan.FromMilliseconds(150)));
        var progress = new RecordingProgress<AiConnectionProbeProgress>();

        var result = await manager.TestAsync(CreateProfile(), progress);

        Assert.IsTrue(result.SupportsChat);
        Assert.IsTrue(result.SupportsStreaming);
        Assert.IsTrue(result.SupportsTools);
        Assert.IsFalse(result.SupportsStreamingTools);
        Assert.AreEqual(AiConnectionProbeService.PartialProbeTimeoutCode, result.ErrorCode);
        CollectionAssert.AreEqual(
            new[]
            {
                AiConnectionProbeStage.BasicChat,
                AiConnectionProbeStage.Streaming,
                AiConnectionProbeStage.Tools,
                AiConnectionProbeStage.StreamingTools,
            },
            progress.Items.Select(item => item.Stage).ToArray());
    }

    [TestMethod]
    public async Task CancelledCapabilityProbeStopsCurrentRequest()
    {
        var root = CreateTemporaryDirectory();
        var gateway = new HangingProbeGateway();
        var manager = new AiConnectionManager(
            new AiConnectionStore(Path.Combine(root, "settings.json")),
            new AiConnectionProbeService(gateway));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await manager.TestAsync(CreateProfile(), cancellationToken: cancellation.Token));

        Assert.AreEqual(1, gateway.SendCount);
    }

    [TestMethod]
    public async Task ProbeFailureWritesSanitizedConnectionDiagnostics()
    {
        var logger = new RecordingLogger<AiConnectionProbeService>();
        var profile = CreateProfile() with
        {
            BaseUri = new Uri("https://models.example.test/v1/"),
            Proxy = new AiProxyConfiguration
            {
                Mode = AiProxyMode.Custom,
                Address = new Uri("http://proxy.example.test:8888"),
            },
        };
        var service = new AiConnectionProbeService(new FailingProbeGateway(), logger);

        var result = await service.ProbeConnectionAsync(profile);

        Assert.IsFalse(result.SupportsChat);
        Assert.AreEqual(1, logger.Entries.Count);
        StringAssert.Contains(logger.Entries[0].Message, "models.example.test");
        StringAssert.Contains(logger.Entries[0].Message, "proxy.example.test:8888");
        StringAssert.Contains(logger.Entries[0].Message, "Custom");
        Assert.IsInstanceOfType<HttpRequestException>(logger.Entries[0].Exception);
        Assert.IsFalse(logger.Entries[0].Message.Contains("actual-secret-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CredentialCleanupDeletesOnlyUnsharedLocalReferences()
    {
        var store = new AiCredentialStore();
        var unique = $"memory:cleanup/{Guid.NewGuid():N}/unique";
        var shared = $"memory:cleanup/{Guid.NewGuid():N}/shared";
        await store.SetAsync(unique, "unique-secret".AsMemory());
        await store.SetAsync(shared, "shared-secret".AsMemory());

        var deleted = await AiCredentialReferenceCleanup.DeleteUnusedLocalReferencesAsync(
            store,
            [unique, shared, "env:READ_ONLY_SECRET"],
            [shared]);

        Assert.AreEqual(1, deleted);
        Assert.IsFalse(await store.ExistsAsync(unique));
        Assert.IsTrue(await store.ExistsAsync(shared));
        await store.DeleteAsync(shared);
    }

    private static AiConnectionProfile CreateProfile() => new()
    {
        Id = "internal",
        DisplayName = "Internal",
        Protocol = AiProtocol.OpenAiResponses,
        BaseUri = new Uri("https://models.example.test/v1/"),
        Model = "glm-test",
        ContextWindowTokens = 1_000_000,
        ContextCompressionThresholdPercent = 70,
        AutomaticContextCompression = true,
        Authentication = new AiAuthenticationConfiguration
        {
            Kind = AiAuthenticationKind.Bearer,
            CredentialReference = "diary.ai/internal/api-key",
        },
        Proxy = new AiProxyConfiguration { Mode = AiProxyMode.Direct },
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Diary.AgentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ProbeGateway : IAgentModelGateway
    {
        public int SendCount { get; private set; }

        public ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            if (request.Tools.Count == 0)
                return ValueTask.FromResult(new AgentModelResponse("ok", [], "stop", null));
            if (request.Messages.Any(message => message.Role == AgentMessageRole.Tool))
                return ValueTask.FromResult(new AgentModelResponse("tool complete", [], "stop", null));
            using var arguments = JsonDocument.Parse("{\"value\":\"probe\"}");
            var selectedTools = request.AllowParallelToolCalls
                ? request.Tools
                : request.Tools.Where(tool => tool.Name == (request.RequiredToolName ?? request.Tools[0].Name));
            return ValueTask.FromResult(new AgentModelResponse(
                string.Empty,
                selectedTools.Select((tool, index) => new AgentToolCall(
                    $"probe-call-{index}",
                    tool.Name,
                    arguments.RootElement.Clone())).ToArray(),
                "tool_calls",
                null));
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
            if (request.Tools.Count == 0 || request.Messages.Any(message => message.Role == AgentMessageRole.Tool))
            {
                yield return new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: "ok");
                yield return new AgentStreamEvent(AgentStreamEventKind.ResponseCompleted, FinishReason: "stop");
            }
            else
            {
                var tool = request.Tools[0];
                yield return new AgentStreamEvent(
                    AgentStreamEventKind.ToolCallStarted,
                    ToolCallId: "stream-probe-call",
                    ToolName: tool.Name,
                    ToolIndex: 0);
                yield return new AgentStreamEvent(
                    AgentStreamEventKind.ToolArgumentsDelta,
                    Text: "{\"value\":\"probe\"}",
                    ToolCallId: "stream-probe-call",
                    ToolName: tool.Name,
                    ToolIndex: 0);
                yield return new AgentStreamEvent(
                    AgentStreamEventKind.ToolCallCompleted,
                    ToolCallId: "stream-probe-call",
                    ToolName: tool.Name,
                    ToolIndex: 0);
                yield return new AgentStreamEvent(AgentStreamEventKind.ResponseCompleted, FinishReason: "tool_calls");
            }
            await Task.CompletedTask;
        }
    }

    private sealed class HangingProbeGateway : IAgentModelGateway
    {
        public int SendCount { get; private set; }

        public async ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("不可达");
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class LateHangingProbeGateway : IAgentModelGateway
    {
        private int _streamCount;

        public ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default)
        {
            if (request.Tools.Count == 0 || request.Messages.Any(message => message.Role == AgentMessageRole.Tool))
                return ValueTask.FromResult(new AgentModelResponse("ok", [], "stop", null));
            using var arguments = JsonDocument.Parse("{\"value\":\"probe\"}");
            return ValueTask.FromResult(new AgentModelResponse(
                string.Empty,
                [new AgentToolCall("probe-call", request.Tools[0].Name, arguments.RootElement.Clone())],
                "tool_calls",
                null));
        }

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _streamCount++;
            if (_streamCount == 1)
            {
                yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
                yield return new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: "ok");
                yield return new AgentStreamEvent(AgentStreamEventKind.ResponseCompleted, FinishReason: "stop");
                yield break;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingProbeGateway : IAgentModelGateway
    {
        public ValueTask<AgentModelResponse> SendAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<AgentModelResponse>(new HttpRequestException(
                "TLS handshake failed",
                new IOException("unexpected EOF")));

        public async IAsyncEnumerable<AgentStreamEvent> StreamAsync(
            AgentModelRequest request,
            AiConnectionProfile connection,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];

        public void Report(T value) => Items.Add(value);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
