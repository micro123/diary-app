using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;

namespace Diary.Agent.Protocols;

public sealed class AnthropicMessagesAdapter : ProtocolAdapterBase
{
    public override AiProtocol Protocol => AiProtocol.AnthropicMessages;

    public override string DefaultRequestPath => "messages";

    public override HttpRequestMessage CreateRequest(
        AgentModelRequest request,
        AiConnectionProfile connection,
        CredentialValue? credential)
    {
        var messages = new JsonArray();
        foreach (var message in request.Messages)
            AddMessage(messages, message);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["system"] = request.SystemInstruction,
            ["messages"] = messages,
            ["stream"] = request.Stream,
            ["max_tokens"] = request.MaxOutputTokens ?? 4096,
        };
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(tool => new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
            }).ToArray());
            if (connection.Compatibility.SendToolChoice)
            {
                body["tool_choice"] = request.RequiredToolName is null
                    ? new JsonObject
                    {
                        ["type"] = "auto",
                        ["disable_parallel_tool_use"] = !request.AllowParallelToolCalls,
                    }
                    : new JsonObject
                    {
                        ["type"] = "tool",
                        ["name"] = request.RequiredToolName,
                        ["disable_parallel_tool_use"] = !request.AllowParallelToolCalls,
                    };
            }
        }
        var result = CreateJsonRequest(body, connection, credential, DefaultRequestPath);
        result.Headers.TryAddWithoutValidation("anthropic-version", connection.Compatibility.AnthropicVersion);
        return result;
    }

    public override async ValueTask<AgentModelResponse> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new AiModelException(AiModelErrorCategory.Protocol, "missing_content", "Anthropic 响应缺少 content。");
        var text = new List<string>();
        var reasoning = new List<string>();
        var reasoningBlocks = new List<JsonElement>();
        var calls = new List<AgentToolCall>();
        foreach (var block in content.EnumerateArray())
        {
            var type = block.GetProperty("type").GetString();
            if (type == "text")
                text.Add(block.GetProperty("text").GetString() ?? string.Empty);
            else if (type is "thinking" or "redacted_thinking")
            {
                reasoningBlocks.Add(block.Clone());
                if (type == "thinking"
                    && block.TryGetProperty("thinking", out var thinking)
                    && thinking.ValueKind == JsonValueKind.String)
                {
                    reasoning.Add(thinking.GetString() ?? string.Empty);
                }
            }
            else if (type == "tool_use")
            {
                calls.Add(new AgentToolCall(
                    block.GetProperty("id").GetString() ?? throw new JsonException("tool_use 缺少 id。"),
                    block.GetProperty("name").GetString() ?? throw new JsonException("tool_use 缺少 name。"),
                    block.GetProperty("input").Clone()));
            }
        }
        AgentUsage? usage = null;
        if (root.TryGetProperty("usage", out var usageElement))
        {
            var input = TryGetInt64(usageElement, "input_tokens");
            var output = TryGetInt64(usageElement, "output_tokens");
            usage = new AgentUsage(input, output, input + output);
        }
        var stopReason = root.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null;
        return new AgentModelResponse(
            string.Concat(text),
            calls,
            stopReason,
            usage,
            ReasoningText: string.Concat(reasoning),
            ReasoningContentBlocks: reasoningBlocks);
    }

    public override async IAsyncEnumerable<AgentStreamEvent> ParseStreamAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new AgentStreamEvent(AgentStreamEventKind.ResponseStarted);
        var calls = new Dictionary<int, (string? Id, string? Name)>();
        var thinkingBlocks = new Dictionary<int, AnthropicThinkingBlockBuilder>();
        string? finishReason = null;
        long? inputTokens = null;
        long? outputTokens = null;
        var terminalSeen = false;
        await foreach (var sse in SseEventReader.ReadAsync(stream, cancellationToken: cancellationToken))
        {
            using var document = ParseEvent(sse.Data);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : sse.Event;
            switch (type)
            {
                case "message_start":
                    if (root.TryGetProperty("message", out var message)
                        && message.TryGetProperty("usage", out var startUsage))
                    {
                        inputTokens = TryGetInt64(startUsage, "input_tokens");
                    }
                    break;
                case "content_block_start":
                    {
                        var index = root.GetProperty("index").GetInt32();
                        var block = root.GetProperty("content_block");
                        var blockType = block.GetProperty("type").GetString();
                        if (blockType is "thinking" or "redacted_thinking")
                        {
                            var builder = AnthropicThinkingBlockBuilder.Create(block);
                            thinkingBlocks[index] = builder;
                            yield return new AgentStreamEvent(
                                AgentStreamEventKind.ReasoningDelta,
                                Text: string.IsNullOrEmpty(builder.InitialText) ? null : builder.InitialText);
                            break;
                        }
                        if (blockType != "tool_use")
                            break;
                        var id = block.GetProperty("id").GetString();
                        var name = block.GetProperty("name").GetString();
                        calls[index] = (id, name);
                        yield return new AgentStreamEvent(
                            AgentStreamEventKind.ToolCallStarted,
                            ToolCallId: id,
                            ToolName: name,
                            ToolIndex: index);
                        break;
                    }
                case "content_block_delta":
                    {
                        var index = root.GetProperty("index").GetInt32();
                        var delta = root.GetProperty("delta");
                        var deltaType = delta.GetProperty("type").GetString();
                        if (deltaType == "text_delta")
                        {
                            yield return new AgentStreamEvent(
                                AgentStreamEventKind.TextDelta,
                                Text: delta.GetProperty("text").GetString());
                        }
                        else if (deltaType == "thinking_delta")
                        {
                            var thinking = delta.TryGetProperty("thinking", out var thinkingElement)
                                ? thinkingElement.GetString() ?? string.Empty
                                : string.Empty;
                            if (!thinkingBlocks.TryGetValue(index, out var builder))
                            {
                                builder = AnthropicThinkingBlockBuilder.CreateThinking();
                                thinkingBlocks[index] = builder;
                            }
                            builder.AppendThinking(thinking);
                            yield return new AgentStreamEvent(
                                AgentStreamEventKind.ReasoningDelta,
                                Text: string.IsNullOrEmpty(thinking) ? null : thinking);
                        }
                        else if (deltaType == "signature_delta")
                        {
                            if (!thinkingBlocks.TryGetValue(index, out var builder))
                            {
                                builder = AnthropicThinkingBlockBuilder.CreateThinking();
                                thinkingBlocks[index] = builder;
                            }
                            if (delta.TryGetProperty("signature", out var signature))
                                builder.AppendSignature(signature.GetString() ?? string.Empty);
                            yield return new AgentStreamEvent(AgentStreamEventKind.ReasoningDelta);
                        }
                        else if (deltaType == "input_json_delta")
                        {
                            calls.TryGetValue(index, out var call);
                            yield return new AgentStreamEvent(
                                AgentStreamEventKind.ToolArgumentsDelta,
                                Text: delta.GetProperty("partial_json").GetString(),
                                ToolCallId: call.Id,
                                ToolName: call.Name,
                                ToolIndex: index);
                        }
                        break;
                    }
                case "content_block_stop":
                    {
                        var index = root.GetProperty("index").GetInt32();
                        if (calls.TryGetValue(index, out var call))
                        {
                            yield return new AgentStreamEvent(
                                AgentStreamEventKind.ToolCallCompleted,
                                ToolCallId: call.Id,
                                ToolName: call.Name,
                                ToolIndex: index);
                        }
                        break;
                    }
                case "message_delta":
                    if (root.TryGetProperty("delta", out var messageDelta)
                        && messageDelta.TryGetProperty("stop_reason", out var stopReason))
                    {
                        finishReason = stopReason.GetString();
                    }
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        outputTokens = TryGetInt64(usage, "output_tokens");
                        yield return new AgentStreamEvent(
                            AgentStreamEventKind.UsageUpdated,
                            Usage: new AgentUsage(inputTokens, outputTokens, inputTokens + outputTokens));
                    }
                    break;
                case "message_stop":
                    terminalSeen = true;
                    break;
                case "error":
                    yield return new AgentStreamEvent(
                        AgentStreamEventKind.ProtocolError,
                        Text: ReadError(root),
                        ErrorCode: "anthropic_error");
                    break;
            }
        }
        if (!terminalSeen)
        {
            throw new AiModelException(
                AiModelErrorCategory.Protocol,
                "stream_incomplete",
                "Anthropic 流在 message_stop 前中断。");
        }
        yield return new AgentStreamEvent(
            AgentStreamEventKind.ResponseCompleted,
            FinishReason: finishReason,
            ReasoningContentBlocks: thinkingBlocks
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value.Build())
                .ToArray());
    }

    private static void AddMessage(JsonArray messages, AgentMessage message)
    {
        if (message.Role == AgentMessageRole.Tool)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = message.ToolCallId,
                        ["content"] = message.Text,
                    },
                },
            });
            return;
        }
        var content = new JsonArray();
        foreach (var block in message.ReasoningContentBlocks)
            content.Add(JsonNode.Parse(block.GetRawText()));
        if (!string.IsNullOrEmpty(message.Text))
            content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Text });
        foreach (var call in message.ToolCalls)
        {
            content.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = call.Id,
                ["name"] = call.Name,
                ["input"] = JsonNode.Parse(call.Arguments.GetRawText()),
            });
        }
        messages.Add(new JsonObject
        {
            ["role"] = message.Role == AgentMessageRole.User ? "user" : "assistant",
            ["content"] = content,
        });
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
                "Anthropic 流包含无效 JSON。",
                innerException: exception);
        }
    }

    private static string ReadError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error)
            && error.TryGetProperty("message", out var message))
        {
            return message.GetString() ?? "Anthropic 流返回错误。";
        }
        return "Anthropic 流返回错误。";
    }

    private sealed class AnthropicThinkingBlockBuilder
    {
        private readonly string _type;
        private readonly StringBuilder _thinking = new();
        private readonly StringBuilder _signature = new();
        private readonly JsonElement? _rawBlock;

        private AnthropicThinkingBlockBuilder(string type, JsonElement? rawBlock = null)
        {
            _type = type;
            _rawBlock = rawBlock;
        }

        public string InitialText { get; private set; } = string.Empty;

        public static AnthropicThinkingBlockBuilder Create(JsonElement block)
        {
            var type = block.GetProperty("type").GetString() ?? "thinking";
            if (type == "redacted_thinking")
                return new AnthropicThinkingBlockBuilder(type, block.Clone());
            var builder = CreateThinking();
            if (block.TryGetProperty("thinking", out var thinking)
                && thinking.ValueKind == JsonValueKind.String)
            {
                builder.InitialText = thinking.GetString() ?? string.Empty;
                builder._thinking.Append(builder.InitialText);
            }
            if (block.TryGetProperty("signature", out var signature)
                && signature.ValueKind == JsonValueKind.String)
            {
                builder._signature.Append(signature.GetString());
            }
            return builder;
        }

        public static AnthropicThinkingBlockBuilder CreateThinking() => new("thinking");

        public void AppendThinking(string value) => _thinking.Append(value);

        public void AppendSignature(string value) => _signature.Append(value);

        public JsonElement Build()
        {
            if (_rawBlock is { } rawBlock)
                return rawBlock;
            var block = new JsonObject
            {
                ["type"] = _type,
                ["thinking"] = _thinking.ToString(),
            };
            if (_signature.Length > 0)
                block["signature"] = _signature.ToString();
            return JsonSerializer.SerializeToElement(block);
        }
    }
}
