using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Diary.Agent.Protocols;

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

public sealed class AiConnectionProbeService(IAgentModelGateway gateway)
{
    private static readonly JsonElement ProbeSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}
        """).RootElement.Clone();

    public async ValueTask<AiConnectionCapabilities> ProbeAsync(
        AiConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var chatRequest = new AgentModelRequest(
                profile.Model,
                "Reply with a short acknowledgement.",
                [AgentMessage.User("Connection probe")],
                [],
                false,
                32);
            _ = await gateway.SendAsync(chatRequest, profile, cancellationToken);

            var streaming = await ProbeStreamingAsync(profile, cancellationToken);
            var tools = await ProbeToolsAsync(profile, cancellationToken);
            var streamingTools = tools && await ProbeStreamingToolsAsync(profile, cancellationToken);
            var parallelTools = tools && await ProbeParallelToolsAsync(profile, cancellationToken);
            var forcedToolChoice = tools && await ProbeForcedToolChoiceAsync(profile, cancellationToken);
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
            return new AiConnectionCapabilities(
                false,
                false,
                false,
                DateTimeOffset.UtcNow,
                exception.Code,
                exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new AiConnectionCapabilities(
                false,
                false,
                false,
                DateTimeOffset.UtcNow,
                "probe_failed",
                "连接探测失败；详细信息仅记录在本地诊断中。");
        }
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
            await foreach (var item in gateway.StreamAsync(request, profile, cancellationToken))
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
            var first = await gateway.SendAsync(firstRequest, profile, cancellationToken);
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
            var second = await gateway.SendAsync(secondRequest, profile, cancellationToken);
            return second.ToolCalls.Count == 0;
        }
        catch (Exception exception) when (exception is AiModelException or InvalidOperationException or JsonException)
        {
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
            var first = await gateway.SendAsync(firstRequest, profile, cancellationToken);
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
            var second = await gateway.SendAsync(secondRequest, profile, cancellationToken);
            return second.ToolCalls.Count == 0;
        }
        catch (Exception exception) when (exception is AiModelException
                                           or InvalidOperationException
                                           or HttpRequestException
                                           or JsonException)
        {
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
            var response = await gateway.SendAsync(request, profile, cancellationToken);
            return response.ToolCalls.Count == 1
                   && response.ToolCalls[0].Name == definition.Name
                   && response.ToolCalls[0].Arguments.ValueKind == JsonValueKind.Object;
        }
        catch (Exception exception) when (exception is AiModelException
                                           or InvalidOperationException
                                           or HttpRequestException
                                           or JsonException)
        {
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
        await foreach (var item in gateway.StreamAsync(request, profile, cancellationToken))
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
        var result = await _probe.ProbeAsync(workingCopy, cancellationToken);
        _capabilities[workingCopy.Id] = result;
        return result;
    }
}
