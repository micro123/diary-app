using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;

namespace Diary.Agent.Protocols;

public enum AgentMessageRole
{
    User,
    Assistant,
    Tool,
}

public sealed record AgentToolCall(string Id, string Name, JsonElement Arguments);

public sealed record AgentMessage
{
    public required AgentMessageRole Role { get; init; }

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<AgentToolCall> ToolCalls { get; init; } = [];

    public string? ToolCallId { get; init; }

    public string ReasoningText { get; init; } = string.Empty;

    public IReadOnlyList<JsonElement> ReasoningContentBlocks { get; init; } = [];

    public static AgentMessage User(string text) => new() { Role = AgentMessageRole.User, Text = text };

    public static AgentMessage Assistant(
        string text,
        IReadOnlyList<AgentToolCall>? toolCalls = null,
        string? reasoningText = null,
        IReadOnlyList<JsonElement>? reasoningContentBlocks = null) =>
        new()
        {
            Role = AgentMessageRole.Assistant,
            Text = text,
            ToolCalls = toolCalls ?? [],
            ReasoningText = reasoningText ?? string.Empty,
            ReasoningContentBlocks = reasoningContentBlocks ?? [],
        };

    public static AgentMessage Tool(string toolCallId, string text) =>
        new() { Role = AgentMessageRole.Tool, ToolCallId = toolCallId, Text = text };
}

public sealed record AgentToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema);

public abstract record AgentProtocolState;

public sealed record OpenAiResponsesProtocolState(
    IReadOnlyList<JsonElement> OutputItems) : AgentProtocolState;

public sealed record AgentModelRequest(
    string Model,
    string SystemInstruction,
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<AgentToolDefinition> Tools,
    bool Stream,
    int? MaxOutputTokens,
    AgentProtocolState? ProtocolState = null,
    string? RequiredToolName = null,
    bool AllowParallelToolCalls = false);

public sealed record AgentUsage(long? InputTokens, long? OutputTokens, long? TotalTokens);

public sealed record AgentModelResponse(
    string Text,
    IReadOnlyList<AgentToolCall> ToolCalls,
    string? FinishReason,
    AgentUsage? Usage,
    AgentProtocolState? ProtocolState = null,
    string? ReasoningText = null,
    IReadOnlyList<JsonElement>? ReasoningContentBlocks = null);

public enum AgentStreamEventKind
{
    ResponseStarted,
    ReasoningDelta,
    TextDelta,
    ToolCallStarted,
    ToolArgumentsDelta,
    ToolCallCompleted,
    UsageUpdated,
    ResponseCompleted,
    ProtocolError,
}

public sealed record AgentStreamEvent(
    AgentStreamEventKind Kind,
    string? Text = null,
    string? ToolCallId = null,
    string? ToolName = null,
    int? ToolIndex = null,
    AgentUsage? Usage = null,
    string? FinishReason = null,
    string? ErrorCode = null,
    AgentProtocolState? ProtocolState = null,
    IReadOnlyList<JsonElement>? ReasoningContentBlocks = null);

public interface IAgentModelGateway
{
    ValueTask<AgentModelResponse> SendAsync(
        AgentModelRequest request,
        AiConnectionProfile connection,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgentStreamEvent> StreamAsync(
        AgentModelRequest request,
        AiConnectionProfile connection,
        CancellationToken cancellationToken = default);
}

public interface IAiProtocolAdapter
{
    AiProtocol Protocol { get; }

    string DefaultRequestPath { get; }

    HttpRequestMessage CreateRequest(
        AgentModelRequest request,
        AiConnectionProfile connection,
        CredentialValue? credential);

    ValueTask<AgentModelResponse> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AgentStreamEvent> ParseStreamAsync(
        Stream stream,
        CancellationToken cancellationToken);
}

public enum AiModelErrorCategory
{
    Configuration,
    Authentication,
    NotFound,
    RateLimit,
    Proxy,
    Timeout,
    Service,
    Protocol,
    Cancelled,
    Tls,
    Network,
}

public sealed class AiModelException(
    AiModelErrorCategory category,
    string code,
    string message,
    int? statusCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public AiModelErrorCategory Category { get; } = category;

    public string Code { get; } = code;

    public int? StatusCode { get; } = statusCode;
}
