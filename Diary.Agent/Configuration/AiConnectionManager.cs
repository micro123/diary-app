using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Diary.Agent.Protocols;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.Agent.Configuration;

public sealed record AiConnectionCapabilities(
    bool SupportsChat,
    bool SupportsStreaming,
    bool SupportsTools,
    DateTimeOffset TestedAtUtc,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool SupportsStreamingTools = false,
    bool SupportsParallelTools = false,
    bool SupportsForcedToolChoice = false);

public enum AiConnectionProbeStage
{
    BasicChat,
    Streaming,
    Tools,
    StreamingTools,
    ParallelTools,
    ForcedToolChoice,
}

public sealed record AiConnectionProbeProgress(
    AiConnectionProbeStage Stage,
    int Current,
    int Total,
    string DisplayName);

public sealed class AiConnectionProbeService
{
    public const string BasicProbeOnlyCode = "basic_probe_only";
    private static readonly TimeSpan BasicProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FullProbeTimeout = TimeSpan.FromSeconds(90);
    private static readonly JsonElement ProbeSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}
        """).RootElement.Clone();
    private readonly IAgentModelGateway _gateway;
    private readonly ILogger<AiConnectionProbeService> _logger;

    public AiConnectionProbeService(
        IAgentModelGateway gateway,
        ILogger<AiConnectionProbeService>? logger = null)
    {
        _gateway = gateway;
        _logger = logger ?? NullLogger<AiConnectionProbeService>.Instance;
    }

    public async ValueTask<AiConnectionCapabilities> ProbeConnectionAsync(
        AiConnectionProfile profile,
        IProgress<AiConnectionProbeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var budget = new CancellationTokenSource(BasicProbeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        Report(progress, AiConnectionProbeStage.BasicChat, 1, 1, "基础连接");
        try
        {
            await SendBasicChatAsync(profile, linked.Token);
            return new AiConnectionCapabilities(
                true,
                false,
                false,
                DateTimeOffset.UtcNow,
                BasicProbeOnlyCode,
                "基础连接成功；尚未执行完整能力探测。");
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            if (budget.IsCancellationRequested)
            {
                LogFailure(profile, "基础连接", exception, "probe_timeout");
                return Failed("probe_timeout", "基础连接测试超过 30 秒，已停止。", exception, profile, log: false);
            }
            return Failed(GetErrorCode(exception), GetErrorMessage(exception), exception, profile);
        }
    }

    public async ValueTask<AiConnectionCapabilities> ProbeAsync(
        AiConnectionProfile profile,
        IProgress<AiConnectionProbeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var budget = new CancellationTokenSource(FullProbeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var probeToken = linked.Token;
        try
        {
            Report(progress, AiConnectionProbeStage.BasicChat, 1, 6, "普通对话");
            await SendBasicChatAsync(profile, probeToken);
            probeToken.ThrowIfCancellationRequested();

            Report(progress, AiConnectionProbeStage.Streaming, 2, 6, "流式文本");
            var streaming = await ProbeStreamingAsync(profile, probeToken);
            probeToken.ThrowIfCancellationRequested();

            Report(progress, AiConnectionProbeStage.Tools, 3, 6, "工具闭环");
            var tools = await ProbeToolsAsync(profile, probeToken);
            probeToken.ThrowIfCancellationRequested();

            Report(progress, AiConnectionProbeStage.StreamingTools, 4, 6, "流式工具");
            var streamingTools = tools && await ProbeStreamingToolsAsync(profile, probeToken);
            probeToken.ThrowIfCancellationRequested();

            Report(progress, AiConnectionProbeStage.ParallelTools, 5, 6, "并行工具");
            var parallelTools = tools && await ProbeParallelToolsAsync(profile, probeToken);
            probeToken.ThrowIfCancellationRequested();

            Report(progress, AiConnectionProbeStage.ForcedToolChoice, 6, 6, "强制工具选择");
            var forcedToolChoice = tools && await ProbeForcedToolChoiceAsync(profile, probeToken);
            probeToken.ThrowIfCancellationRequested();
            return new AiConnectionCapabilities(
                true,
                streaming,
                tools,
                DateTimeOffset.UtcNow,
                SupportsStreamingTools: streamingTools,
                SupportsParallelTools: parallelTools,
                SupportsForcedToolChoice: forcedToolChoice);
        }
        catch (AiModelException exception)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            if (budget.IsCancellationRequested)
            {
                LogFailure(profile, "完整能力探测", exception, "probe_timeout");
                return Failed("probe_timeout", "完整能力探测超过 90 秒，已停止。", exception, profile, log: false);
            }
            return Failed(exception.Code, exception.Message, exception, profile);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("连接测试已取消。", exception, cancellationToken);
        }
        catch (OperationCanceledException exception) when (budget.IsCancellationRequested)
        {
            LogFailure(profile, "完整能力探测", exception, "probe_timeout");
            return Failed("probe_timeout", "完整能力探测超过 90 秒，已停止。", exception, profile, log: false);
        }
        catch (Exception exception)
        {
            return Failed("probe_failed", "连接探测失败，请查看本地诊断日志。", exception, profile);
        }
    }

    private async ValueTask SendBasicChatAsync(
        AiConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var chatRequest = new AgentModelRequest(
            profile.Model,
            "Reply with a short acknowledgement.",
            [AgentMessage.User("Connection probe")],
            [],
            false,
            32);
        _ = await _gateway.SendAsync(chatRequest, profile, cancellationToken);
    }

    private async ValueTask<bool> ProbeStreamingAsync(
        AiConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var completed = false;
            var request = new AgentModelRequest(
                profile.Model,
                "Reply with one short word.",
                [AgentMessage.User("Streaming probe")],
                [],
                true,
                32);
            await foreach (var item in _gateway.StreamAsync(request, profile, cancellationToken))
            {
                if (item.Kind == AgentStreamEventKind.ProtocolError)
                    return false;
                if (item.Kind == AgentStreamEventKind.ResponseCompleted)
                    completed = true;
            }
            return completed;
        }
        catch (Exception exception) when (exception is AiModelException or HttpRequestException or JsonException)
        {
            ThrowIfCancelled(cancellationToken);
            LogCapabilityFailure(profile, "流式文本", exception);
            return false;
        }
    }

    private async ValueTask<bool> ProbeToolsAsync(
        AiConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var definition = new AgentToolDefinition(
                "diary_probe_echo",
                "Return the provided value. Use this tool now with value 'probe'.",
                ProbeSchema);
            var firstRequest = new AgentModelRequest(
                profile.Model,
                "You are testing function calling. Call diary_probe_echo exactly once.",
                [AgentMessage.User("Call the probe tool with value probe.")],
                [definition],
                false,
                64);
            var first = await _gateway.SendAsync(firstRequest, profile, cancellationToken);
            var call = first.ToolCalls.SingleOrDefault(item => item.Name == definition.Name);
            if (call is null)
                return false;
            var secondRequest = firstRequest with
            {
                SystemInstruction = "The probe tool has returned. Reply with a short final acknowledgement without calling more tools.",
                Messages =
                [
                    .. firstRequest.Messages,
                    AgentMessage.Assistant(first.Text, first.ToolCalls),
                    AgentMessage.Tool(call.Id, "{\"value\":\"probe\"}"),
                ],
                ProtocolState = first.ProtocolState,
            };
            var second = await _gateway.SendAsync(secondRequest, profile, cancellationToken);
            return second.ToolCalls.Count == 0;
        }
        catch (Exception exception) when (exception is AiModelException or InvalidOperationException or JsonException)
        {
            ThrowIfCancelled(cancellationToken);
            LogCapabilityFailure(profile, "工具闭环", exception);
            return false;
        }
    }

    private async ValueTask<bool> ProbeStreamingToolsAsync(
        AiConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var definition = CreateProbeDefinition("diary_probe_stream_echo");
            var firstRequest = new AgentModelRequest(
                profile.Model,
                "You are testing streamed function calling. Call diary_probe_stream_echo exactly once.",
                [AgentMessage.User("Call the streamed probe tool with value probe.")],
                [definition],
                true,
                64);
            var first = await ReadStreamedResponseAsync(firstRequest, profile, cancellationToken);
            var call = first.ToolCalls.SingleOrDefault(item => item.Name == definition.Name);
            if (call is null || !HasProbeValue(call.Arguments))
                return false;
            var secondRequest = firstRequest with
            {
                SystemInstruction = "The streamed probe tool has returned. Reply with a short final acknowledgement without calling more tools.",
                Messages =
                [
                    .. firstRequest.Messages,
                    AgentMessage.Assistant(first.Text, first.ToolCalls),
                    AgentMessage.Tool(call.Id, "{\"value\":\"probe\"}"),
                ],
                ProtocolState = first.ProtocolState,
            };
            var second = await ReadStreamedResponseAsync(secondRequest, profile, cancellationToken);
            return second.ToolCalls.Count == 0;
        }
        catch (Exception exception) when (exception is AiModelException
                                           or InvalidOperationException
                                           or HttpRequestException
                                           or JsonException)
        {
            ThrowIfCancelled(cancellationToken);
            LogCapabilityFailure(profile, "流式工具", exception);
            return false;
        }
    }

    private async ValueTask<bool> ProbeParallelToolsAsync(
        AiConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var firstDefinition = CreateProbeDefinition("diary_probe_parallel_a");
            var secondDefinition = CreateProbeDefinition("diary_probe_parallel_b");
            var firstRequest = new AgentModelRequest(
                profile.Model,
                "You are testing parallel function calling. Call both supplied tools in one response.",
                [AgentMessage.User("Call both probe tools with value probe.")],
                [firstDefinition, secondDefinition],
                false,
                96,
                AllowParallelToolCalls: true);
            var first = await _gateway.SendAsync(firstRequest, profile, cancellationToken);
            var expectedNames = new HashSet<string>(
                [firstDefinition.Name, secondDefinition.Name],
                StringComparer.Ordinal);
            if (first.ToolCalls.Count != 2
                || !first.ToolCalls.All(call => expectedNames.Remove(call.Name) && HasProbeValue(call.Arguments))
                || expectedNames.Count != 0)
            {
                return false;
            }
            var secondRequest = firstRequest with
            {
                SystemInstruction = "Both parallel probe tools have returned. Reply with a short final acknowledgement without calling more tools.",
                Messages =
                [
                    .. firstRequest.Messages,
                    AgentMessage.Assistant(first.Text, first.ToolCalls),
                    .. first.ToolCalls.Select(call => AgentMessage.Tool(call.Id, "{\"value\":\"probe\"}")),
                ],
                ProtocolState = first.ProtocolState,
            };
            var second = await _gateway.SendAsync(secondRequest, profile, cancellationToken);
            return second.ToolCalls.Count == 0;
        }
        catch (Exception exception) when (exception is AiModelException
                                           or InvalidOperationException
                                           or HttpRequestException
                                           or JsonException)
        {
            ThrowIfCancelled(cancellationToken);
            LogCapabilityFailure(profile, "并行工具", exception);
            return false;
        }
    }

    private async ValueTask<bool> ProbeForcedToolChoiceAsync(
        AiConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (!profile.Compatibility.SendToolChoice)
            return false;
        try
        {
            var definition = CreateProbeDefinition("diary_probe_forced_echo");
            var request = new AgentModelRequest(
                profile.Model,
                "Reply normally unless the request forces a tool.",
                [AgentMessage.User("Return a normal text response.")],
                [definition],
                false,
                64,
                RequiredToolName: definition.Name);
            var response = await _gateway.SendAsync(request, profile, cancellationToken);
            return response.ToolCalls.Count == 1
                   && response.ToolCalls[0].Name == definition.Name
                   && response.ToolCalls[0].Arguments.ValueKind == JsonValueKind.Object;
        }
        catch (Exception exception) when (exception is AiModelException
                                           or InvalidOperationException
                                           or HttpRequestException
                                           or JsonException)
        {
            ThrowIfCancelled(cancellationToken);
            LogCapabilityFailure(profile, "强制工具选择", exception);
            return false;
        }
    }

    private async ValueTask<AgentModelResponse> ReadStreamedResponseAsync(
        AgentModelRequest request,
        AiConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var calls = new Dictionary<int, StreamedProbeToolCall>();
        AgentProtocolState? protocolState = null;
        var completed = false;
        await foreach (var item in _gateway.StreamAsync(request, profile, cancellationToken))
        {
            switch (item.Kind)
            {
                case AgentStreamEventKind.TextDelta:
                    text.Append(item.Text);
                    break;
                case AgentStreamEventKind.ToolCallStarted:
                    calls[item.ToolIndex ?? calls.Count] = new StreamedProbeToolCall(item.ToolCallId, item.ToolName);
                    break;
                case AgentStreamEventKind.ToolArgumentsDelta:
                    if (!calls.TryGetValue(item.ToolIndex ?? 0, out var call))
                        return InvalidStreamedResponse();
                    call.Arguments.Append(item.Text);
                    break;
                case AgentStreamEventKind.ToolCallCompleted:
                    if (!calls.TryGetValue(item.ToolIndex ?? 0, out call))
                        return InvalidStreamedResponse();
                    call.Completed = true;
                    break;
                case AgentStreamEventKind.ResponseCompleted:
                    completed = true;
                    protocolState = item.ProtocolState;
                    break;
                case AgentStreamEventKind.ProtocolError:
                    return InvalidStreamedResponse();
            }
        }
        if (!completed || calls.Values.Any(call => !call.Completed))
            return InvalidStreamedResponse();
        return new AgentModelResponse(
            text.ToString(),
            calls.OrderBy(pair => pair.Key).Select(pair => pair.Value.Build()).ToArray(),
            "completed",
            null,
            protocolState);
    }

    private static AgentModelResponse InvalidStreamedResponse() =>
        throw new AiModelException(
            AiModelErrorCategory.Protocol,
            "stream_probe_invalid",
            "流式工具探测响应不完整。");

    private static AgentToolDefinition CreateProbeDefinition(string name) => new(
        name,
        "Return the provided value. Use value 'probe'.",
        ProbeSchema);

    private static bool HasProbeValue(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty("value", out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() == "probe";

    private AiConnectionCapabilities Failed(
        string code,
        string message,
        Exception exception,
        AiConnectionProfile profile,
        bool log = true)
    {
        if (log)
            LogFailure(profile, "连接测试", exception, code);
        return new AiConnectionCapabilities(
            false,
            false,
            false,
            DateTimeOffset.UtcNow,
            code,
            message);
    }

    private void LogCapabilityFailure(AiConnectionProfile profile, string stage, Exception exception) =>
        LogFailure(profile, stage, exception, GetErrorCode(exception));

    private void LogFailure(
        AiConnectionProfile profile,
        string stage,
        Exception exception,
        string code)
    {
        var modelException = exception as AiModelException;
        _logger.LogWarning(
            exception,
            "AI 连接探测失败。Stage={Stage} ConnectionId={ConnectionId} Protocol={Protocol} BaseUri={BaseUri} RequestPath={RequestPath} Model={Model} ProxyMode={ProxyMode} ProxyAddress={ProxyAddress} ErrorCategory={ErrorCategory} ErrorCode={ErrorCode} HttpStatus={HttpStatus}",
            stage,
            profile.Id,
            profile.Protocol,
            profile.BaseUri.GetLeftPart(UriPartial.Path),
            profile.RequestPathOverride ?? "<protocol-default>",
            profile.Model,
            profile.Proxy.Mode,
            profile.Proxy.Address?.GetLeftPart(UriPartial.Authority) ?? "<none>",
            modelException?.Category.ToString() ?? "Unexpected",
            code,
            modelException?.StatusCode);
    }

    private static string GetErrorCode(Exception exception) =>
        exception is AiModelException modelException ? modelException.Code : "probe_failed";

    private static string GetErrorMessage(Exception exception) =>
        exception is AiModelException modelException
            ? modelException.Message
            : "连接探测失败，请查看本地诊断日志。";

    private static void ThrowIfCancelled(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
    }

    private static void Report(
        IProgress<AiConnectionProbeProgress>? progress,
        AiConnectionProbeStage stage,
        int current,
        int total,
        string displayName) =>
        progress?.Report(new AiConnectionProbeProgress(stage, current, total, displayName));

    private sealed class StreamedProbeToolCall(string? id, string? name)
    {
        public StringBuilder Arguments { get; } = new();

        public bool Completed { get; set; }

        public AgentToolCall Build()
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                throw new JsonException("流式工具调用缺少 ID 或名称。");
            using var document = JsonDocument.Parse(Arguments.Length == 0 ? "{}" : Arguments.ToString());
            return new AgentToolCall(id, name, document.RootElement.Clone());
        }
    }
}

public sealed class AiConnectionManager
{
    private readonly AiConnectionStore _store;
    private readonly AiConnectionProbeService _probe;
    private readonly ConcurrentDictionary<string, AiConnectionCapabilities> _capabilities = new(StringComparer.Ordinal);

    public AiConnectionManager(AiConnectionStore store, AiConnectionProbeService probe)
    {
        _store = store;
        _probe = probe;
        var result = store.Load();
        LoadStatus = result.Status;
        LoadError = result.Error;
        Settings = result.Settings ?? new AiAgentSettings();
        foreach (var pair in Settings.Capabilities)
            _capabilities[pair.Key] = pair.Value;
    }

    public AiAgentSettings Settings { get; private set; }

    public AiSettingsLoadStatus LoadStatus { get; private set; }

    public string? LoadError { get; private set; }

    public IReadOnlyDictionary<string, AiConnectionCapabilities> Capabilities => _capabilities;

    public AiConnectionProfile? GetDefaultProfile() => Settings.DefaultProfileId is null
        ? null
        : Settings.Profiles.FirstOrDefault(profile => profile.Id == Settings.DefaultProfileId);

    public void SaveWorkingCopy(AiAgentSettings workingCopy)
    {
        ArgumentNullException.ThrowIfNull(workingCopy);
        _store.Save(workingCopy);
        Settings = workingCopy with { Profiles = workingCopy.Profiles.ToArray() };
        _capabilities.Clear();
        foreach (var pair in Settings.Capabilities)
            _capabilities[pair.Key] = pair.Value;
        LoadStatus = AiSettingsLoadStatus.Loaded;
        LoadError = null;
        var activeIds = Settings.Profiles.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _capabilities.Keys.Where(id => !activeIds.Contains(id)))
            _capabilities.TryRemove(id, out _);
    }

    public async ValueTask<AiConnectionCapabilities> TestAsync(
        AiConnectionProfile workingCopy,
        IProgress<AiConnectionProbeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = AiConnectionProfileValidator.Validate(workingCopy);
        if (errors.Count > 0)
        {
            return new AiConnectionCapabilities(
                false,
                false,
                false,
                DateTimeOffset.UtcNow,
                "configuration_invalid",
                string.Join(" ", errors));
        }
        var result = await _probe.ProbeAsync(workingCopy, progress, cancellationToken);
        _capabilities[workingCopy.Id] = result;
        return result;
    }

    public async ValueTask<AiConnectionCapabilities> TestConnectionAsync(
        AiConnectionProfile workingCopy,
        IProgress<AiConnectionProbeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = AiConnectionProfileValidator.Validate(workingCopy);
        if (errors.Count > 0)
        {
            return new AiConnectionCapabilities(
                false,
                false,
                false,
                DateTimeOffset.UtcNow,
                "configuration_invalid",
                string.Join(" ", errors));
        }
        var result = await _probe.ProbeConnectionAsync(workingCopy, progress, cancellationToken);
        _capabilities[workingCopy.Id] = result;
        return result;
    }
}
