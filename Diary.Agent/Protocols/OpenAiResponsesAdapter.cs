using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;

namespace Diary.Agent.Protocols;

public sealed class OpenAiResponsesAdapter : ProtocolAdapterBase
{
    public override AiProtocol Protocol => AiProtocol.OpenAiResponses;

    public override string DefaultRequestPath => "responses";

    public override HttpRequestMessage CreateRequest(
        AgentModelRequest request,
        AiConnectionProfile connection,
        CredentialValue? credential)
    {
        var input = new JsonArray();
        if (request.ProtocolState is OpenAiResponsesProtocolState state)
        {
            foreach (var item in state.OutputItems)
                input.Add(JsonNode.Parse(item.GetRawText()));
        }
        foreach (var message in request.Messages)
            AddInput(input, message);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["instructions"] = request.SystemInstruction,
            ["input"] = input,
            ["store"] = false,
            ["stream"] = request.Stream,
        };
        if (request.MaxOutputTokens is not null)
            body["max_output_tokens"] = request.MaxOutputTokens.Value;
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(tool => new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
                ["strict"] = false,
            }).ToArray());
            if (connection.Compatibility.SendToolChoice)
            {
                body["tool_choice"] = request.RequiredToolName is null
                    ? "auto"
                    : new JsonObject
                    {
                        ["type"] = "function",
                        ["name"] = request.RequiredToolName,
                    };
            }
            body["parallel_tool_calls"] = request.AllowParallelToolCalls;
        }
        return CreateJsonRequest(body, connection, credential, DefaultRequestPath);
    }

    public override async ValueTask<AgentModelResponse> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            throw new AiModelException(AiModelErrorCategory.Protocol, "missing_output", "Responses 响应缺少 output。");
        var text = new List<string>();
        var calls = new List<AgentToolCall>();
        var items = new List<JsonElement>();
        foreach (var item in output.EnumerateArray())
        {
            items.Add(item.Clone());
            var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type == "message" && item.TryGetProperty("content", out var content))
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var blockType)
                        && blockType.GetString() is "output_text" or "text"
                        && block.TryGetProperty("text", out var value))
                    {
                        text.Add(value.GetString() ?? string.Empty);
                    }
                }
            }
            else if (type == "function_call")
            {
                calls.Add(new AgentToolCall(
                    item.GetProperty("call_id").GetString()
                        ?? throw new JsonException("function_call 缺少 call_id。"),
                    item.GetProperty("name").GetString()
                        ?? throw new JsonException("function_call 缺少 name。"),
                    ParseArguments(item.GetProperty("arguments").GetString())));
            }
        }
        var usage = root.TryGetProperty("usage", out var usageElement)
            ? ReadUsage(usageElement, "input_tokens", "output_tokens", "total_tokens")
            : null;
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        return new AgentModelResponse(
            string.Concat(text),
            calls,
            status,
            usage,
            new OpenAiResponsesProtocolState(items));
    }

    public override async IAsyncEnumerable<AgentStreamEvent> ParseStreamAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
        var calls = new Dictionary<int, (string? Id, string? Name)>();
        string? finishReason = null;
        AgentProtocolState? completedState = null;
        var terminalSeen = false;
        await foreach (var sse in SseEventReader.ReadAsync(stream, cancellationToken: cancellationToken))
        {
            if (sse.Data == "[DONE]")
                break;
            using var document = ParseEvent(sse.Data);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : sse.Event;
            switch (type)
            {
                case "response.output_text.delta":
                    yield return new AgentStreamEvent(
                        AgentStreamEventKind.TextDelta,
                        Text: root.GetProperty("delta").GetString());
                    break;
                case "response.output_item.added":
                    {
                        if (!root.TryGetProperty("item", out var item)
                            || !item.TryGetProperty("type", out var itemType)
                            || itemType.GetString() != "function_call")
                        {
                            break;
                        }
                        var index = root.TryGetProperty("output_index", out var outputIndex)
                            ? outputIndex.GetInt32()
                            : 0;
                        var id = item.TryGetProperty("call_id", out var callId) ? callId.GetString() : null;
                        var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                        calls[index] = (id, name);
                        yield return new AgentStreamEvent(
                            AgentStreamEventKind.ToolCallStarted,
                            ToolCallId: id,
                            ToolName: name,
                            ToolIndex: index);
                        break;
                    }
                case "response.function_call_arguments.delta":
                    {
                        var index = root.TryGetProperty("output_index", out var outputIndex)
                            ? outputIndex.GetInt32()
                            : 0;
                        calls.TryGetValue(index, out var call);
                        yield return new AgentStreamEvent(
                            AgentStreamEventKind.ToolArgumentsDelta,
                            Text: root.GetProperty("delta").GetString(),
                            ToolCallId: root.TryGetProperty("call_id", out var callId) ? callId.GetString() : call.Id,
                            ToolName: call.Name,
                            ToolIndex: index);
                        break;
                    }
                case "response.function_call_arguments.done":
                    {
                        var index = root.TryGetProperty("output_index", out var outputIndex)
                            ? outputIndex.GetInt32()
                            : 0;
                        calls.TryGetValue(index, out var call);
                        yield return new AgentStreamEvent(
                            AgentStreamEventKind.ToolCallCompleted,
                            ToolCallId: root.TryGetProperty("call_id", out var callId) ? callId.GetString() : call.Id,
                            ToolName: call.Name,
                            ToolIndex: index);
                        break;
                    }
                case "response.completed":
                    {
                        terminalSeen = true;
                        if (root.TryGetProperty("response", out var completed))
                        {
                            finishReason = completed.TryGetProperty("status", out var status)
                                ? status.GetString()
                                : "completed";
                            if (completed.TryGetProperty("usage", out var usage))
                            {
                                yield return new AgentStreamEvent(
                                    AgentStreamEventKind.UsageUpdated,
                                    Usage: ReadUsage(usage, "input_tokens", "output_tokens", "total_tokens"));
                            }
                            if (completed.TryGetProperty("output", out var output)
                                && output.ValueKind == JsonValueKind.Array)
                            {
                                completedState = new OpenAiResponsesProtocolState(
                                    output.EnumerateArray().Select(item => item.Clone()).ToArray());
                            }
                        }
                        break;
                    }
                case "error":
                case "response.failed":
                    yield return new AgentStreamEvent(
                        AgentStreamEventKind.ProtocolError,
                        Text: ReadStreamError(root),
                        ErrorCode: type);
                    break;
            }
        }
        if (!terminalSeen)
        {
            throw new AiModelException(
                AiModelErrorCategory.Protocol,
                "stream_incomplete",
                "Responses 流在 response.completed 前中断。");
        }
        yield return new AgentStreamEvent(
            AgentStreamEventKind.ResponseCompleted,
            FinishReason: finishReason,
            ProtocolState: completedState);
    }

    private static void AddInput(JsonArray input, AgentMessage message)
    {
        if (message.Role == AgentMessageRole.Tool)
        {
            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = message.ToolCallId,
                ["output"] = message.Text,
            });
            return;
        }
        input.Add(new JsonObject
        {
            ["type"] = "message",
            ["role"] = message.Role == AgentMessageRole.User ? "user" : "assistant",
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = message.Role == AgentMessageRole.User ? "input_text" : "output_text",
                    ["text"] = message.Text,
                },
            },
        });
        foreach (var call in message.ToolCalls)
        {
            input.Add(new JsonObject
            {
                ["type"] = "function_call",
                ["call_id"] = call.Id,
                ["name"] = call.Name,
                ["arguments"] = call.Arguments.GetRawText(),
            });
        }
    }

    private static JsonDocument ParseEvent(string data)
    {
        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException exception)
        {
            throw new AiModelException(
                AiModelErrorCategory.Protocol,
                "invalid_sse_json",
                "Responses 流包含无效 JSON。",
                innerException: exception);
        }
    }

    private static string ReadStreamError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error)
            && error.TryGetProperty("message", out var message))
        {
            return message.GetString() ?? "Responses 流返回错误。";
        }
        return "Responses 流返回错误。";
    }
}
