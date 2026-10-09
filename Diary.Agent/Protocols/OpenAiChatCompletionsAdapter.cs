using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;

namespace Diary.Agent.Protocols;

public sealed class OpenAiChatCompletionsAdapter : ProtocolAdapterBase
{
    public override AiProtocol Protocol => AiProtocol.OpenAiChatCompletions;

    public override string DefaultRequestPath => "chat/completions";

    public override HttpRequestMessage CreateRequest(
        AgentModelRequest request,
        AiConnectionProfile connection,
        CredentialValue? credential)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemInstruction },
        };
        foreach (var message in request.Messages)
            messages.Add(ToMessage(message));
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = request.Stream,
        };
        if (request.MaxOutputTokens is not null)
            body["max_tokens"] = request.MaxOutputTokens.Value;
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(tool => ToFunctionTool(tool)).ToArray());
            body["parallel_tool_calls"] = request.AllowParallelToolCalls;
            if (connection.Compatibility.SendToolChoice)
            {
                body["tool_choice"] = request.RequiredToolName is null
                    ? "auto"
                    : new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = request.RequiredToolName },
                    };
            }
        }
        if (request.Stream && connection.Compatibility.SendStreamOptions)
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        return CreateJsonRequest(body, connection, credential, DefaultRequestPath);
    }

    public override async ValueTask<AgentModelResponse> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new AiModelException(AiModelErrorCategory.Protocol, "missing_choices", "响应缺少 choices。");
        }
        var choice = choices[0];
        var message = choice.GetProperty("message");
        var text = ReadContent(message);
        var reasoningText = ReadReasoningContent(message);
        var calls = ReadToolCalls(message);
        var finishReason = choice.TryGetProperty("finish_reason", out var finish)
            ? finish.GetString()
            : null;
        var usage = root.TryGetProperty("usage", out var usageElement)
            ? ReadUsage(usageElement, "prompt_tokens", "completion_tokens", "total_tokens")
            : null;
        return new AgentModelResponse(text, calls, finishReason, usage, ReasoningText: reasoningText);
    }

    public override async IAsyncEnumerable<AgentStreamEvent> ParseStreamAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
        var knownCalls = new Dictionary<int, (string? Id, string? Name)>();
        string? finishReason = null;
        var terminalSeen = false;
        await foreach (var sse in SseEventReader.ReadAsync(stream, cancellationToken: cancellationToken))
        {
            if (sse.Data == "[DONE]")
            {
                terminalSeen = true;
                break;
            }
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(sse.Data);
            }
            catch (JsonException exception)
            {
                throw new AiModelException(
                    AiModelErrorCategory.Protocol,
                    "invalid_sse_json",
                    "Chat Completions 流包含无效 JSON。",
                    innerException: exception);
            }
            using (document)
            {
                var root = document.RootElement;
                if (root.TryGetProperty("usage", out var usageElement))
                {
                    yield return new AgentStreamEvent(
                        AgentStreamEventKind.UsageUpdated,
                        Usage: ReadUsage(usageElement, "prompt_tokens", "completion_tokens", "total_tokens"));
                }
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                {
                    finishReason = finish.GetString();
                    terminalSeen = true;
                }
                if (!choice.TryGetProperty("delta", out var delta))
                    continue;
                var reasoningDelta = ReadReasoningContent(delta);
                if (!string.IsNullOrEmpty(reasoningDelta))
                {
                    yield return new AgentStreamEvent(
                        AgentStreamEventKind.ReasoningDelta,
                        Text: reasoningDelta);
                }
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    yield return new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: content.GetString());
                }
                if (!delta.TryGetProperty("tool_calls", out var toolCalls))
                    continue;
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    var index = toolCall.GetProperty("index").GetInt32();
                    knownCalls.TryGetValue(index, out var known);
                    var id = toolCall.TryGetProperty("id", out var idElement) ? idElement.GetString() : known.Id;
                    string? name = known.Name;
                    string? arguments = null;
                    if (toolCall.TryGetProperty("function", out var function))
                    {
                        if (function.TryGetProperty("name", out var nameElement))
                            name = nameElement.GetString();
                        if (function.TryGetProperty("arguments", out var argumentsElement))
                            arguments = argumentsElement.GetString();
                    }
                    if (!knownCalls.ContainsKey(index))
                    {
                        knownCalls[index] = (id, name);
                        yield return new AgentStreamEvent(
                            AgentStreamEventKind.ToolCallStarted,
                            ToolCallId: id,
                            ToolName: name,
                            ToolIndex: index);
                    }
                    else
                    {
                        knownCalls[index] = (id, name);
                    }
                    if (!string.IsNullOrEmpty(arguments))
                    {
                        yield return new AgentStreamEvent(
                            AgentStreamEventKind.ToolArgumentsDelta,
                            Text: arguments,
                            ToolCallId: id,
                            ToolName: name,
                            ToolIndex: index);
                    }
                }
            }
        }
        if (!terminalSeen)
        {
            throw new AiModelException(
                AiModelErrorCategory.Protocol,
                "stream_incomplete",
                "Chat Completions 流在完整结束前中断。");
        }
        foreach (var pair in knownCalls.OrderBy(item => item.Key))
        {
            yield return new AgentStreamEvent(
                AgentStreamEventKind.ToolCallCompleted,
                ToolCallId: pair.Value.Id,
                ToolName: pair.Value.Name,
                ToolIndex: pair.Key);
        }
        yield return new AgentStreamEvent(
            AgentStreamEventKind.ResponseCompleted,
            FinishReason: finishReason);
    }

    private static JsonObject ToMessage(AgentMessage message)
    {
        var result = new JsonObject
        {
            ["role"] = message.Role switch
            {
                AgentMessageRole.User => "user",
                AgentMessageRole.Assistant => "assistant",
                AgentMessageRole.Tool => "tool",
                _ => throw new ArgumentOutOfRangeException(nameof(message)),
            },
            ["content"] = message.Text,
        };
        if (message.Role == AgentMessageRole.Tool)
            result["tool_call_id"] = message.ToolCallId;
        if (message.Role == AgentMessageRole.Assistant && !string.IsNullOrEmpty(message.ReasoningText))
            result["reasoning_content"] = message.ReasoningText;
        if (message.ToolCalls.Count > 0)
        {
            result["tool_calls"] = new JsonArray(message.ToolCalls.Select(call => new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.Arguments.GetRawText(),
                },
            }).ToArray());
        }
        return result;
    }

    private static string ReadContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;
        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(content.EnumerateArray()
                .Where(item => item.TryGetProperty("text", out _))
                .Select(item => item.GetProperty("text").GetString()));
        }
        return string.Empty;
    }

    private static string ReadReasoningContent(JsonElement message)
    {
        foreach (var propertyName in new[] { "reasoning_content", "reasoning", "thinking" })
        {
            if (message.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static IReadOnlyList<AgentToolCall> ReadToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var toolCalls))
            return [];
        return toolCalls.EnumerateArray()
            .Select(call =>
            {
                var function = call.GetProperty("function");
                return new AgentToolCall(
                    call.GetProperty("id").GetString() ?? throw new JsonException("工具调用缺少 id。"),
                    function.GetProperty("name").GetString() ?? throw new JsonException("工具调用缺少 name。"),
                    ParseArguments(function.GetProperty("arguments").GetString()));
            })
            .ToArray();
    }
}
