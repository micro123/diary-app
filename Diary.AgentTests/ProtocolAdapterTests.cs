using System.Net;
using System.Text;
using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Protocols;

namespace Diary.AgentTests;

[TestClass]
public sealed class ProtocolAdapterTests
{
    private static readonly JsonElement ToolSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"date":{"type":"string"}},"required":["date"]}
        """).RootElement.Clone();

    [TestMethod]
    public async Task ChatCompletionsRequestAndResponsePreserveToolCallPairing()
    {
        var adapter = new OpenAiChatCompletionsAdapter();
        var request = CreateRequest(AiProtocol.OpenAiChatCompletions, stream: false) with
        {
            Messages =
            [
                AgentMessage.User("查询今天"),
                AgentMessage.Assistant("", [CreateToolCall("call_1")], "先查询数据"),
                AgentMessage.Tool("call_1", "{\"count\":1}"),
            ],
        };

        using var message = adapter.CreateRequest(request, CreateProfile(AiProtocol.OpenAiChatCompletions), new CredentialValue("top-secret"));
        var body = await message.Content!.ReadAsStringAsync();
        StringAssert.Contains(body, "tool_call_id");
        StringAssert.Contains(body, "call_1");
        StringAssert.Contains(body, "reasoning_content");
        using (var requestDocument = JsonDocument.Parse(body))
        {
            Assert.AreEqual(
                "先查询数据",
                requestDocument.RootElement.GetProperty("messages")[2]
                    .GetProperty("reasoning_content").GetString());
        }
        Assert.IsFalse(body.Contains("top-secret", StringComparison.Ordinal));
        Assert.AreEqual("Bearer top-secret", message.Headers.GetValues("Authorization").Single());

        using var response = JsonResponse("""
            {
              "choices":[{"message":{"role":"assistant","content":"完成","reasoning_content":"需要继续核对","tool_calls":[{"id":"call_2","type":"function","function":{"name":"diary_query_work_items","arguments":"{\"date\":\"2026-10-08\"}"}}]},"finish_reason":"tool_calls"}],
              "usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}
            }
            """);
        var parsed = await adapter.ParseResponseAsync(response, CancellationToken.None);
        Assert.AreEqual("完成", parsed.Text);
        Assert.AreEqual("需要继续核对", parsed.ReasoningText);
        Assert.AreEqual("call_2", parsed.ToolCalls.Single().Id);
        Assert.AreEqual("2026-10-08", parsed.ToolCalls.Single().Arguments.GetProperty("date").GetString());
        Assert.AreEqual(15, parsed.Usage?.TotalTokens);
    }

    [TestMethod]
    public async Task ResponsesRequestUsesStoreFalseItemsAndFunctionCallOutput()
    {
        var adapter = new OpenAiResponsesAdapter();
        var previousItem = JsonDocument.Parse("""
            {"type":"function_call","call_id":"call_previous","name":"diary_query_work_items","arguments":"{}"}
            """).RootElement.Clone();
        var request = CreateRequest(AiProtocol.OpenAiResponses, stream: false) with
        {
            Messages = [AgentMessage.Tool("call_previous", "{\"count\":2}")],
            ProtocolState = new OpenAiResponsesProtocolState([previousItem]),
        };

        using var message = adapter.CreateRequest(request, CreateProfile(AiProtocol.OpenAiResponses), new CredentialValue("secret"));
        var body = await message.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.IsFalse(root.GetProperty("store").GetBoolean());
        Assert.IsFalse(root.TryGetProperty("previous_response_id", out _));
        var input = root.GetProperty("input");
        Assert.AreEqual("function_call", input[0].GetProperty("type").GetString());
        Assert.AreEqual("function_call_output", input[1].GetProperty("type").GetString());
        Assert.AreEqual("call_previous", input[1].GetProperty("call_id").GetString());

        using var response = JsonResponse("""
            {
              "status":"completed",
              "output":[
                {"type":"reasoning","summary":[{"type":"summary_text","text":"正在分析"}]},
                {"type":"message","role":"assistant","content":[{"type":"output_text","text":"结果"}]},
                {"type":"function_call","call_id":"call_new","name":"diary_query_work_items","arguments":"{\"date\":\"2026-10-08\"}"}
              ],
              "usage":{"input_tokens":9,"output_tokens":4,"total_tokens":13}
            }
            """);
        var parsed = await adapter.ParseResponseAsync(response, CancellationToken.None);
        Assert.AreEqual("结果", parsed.Text);
        Assert.AreEqual("正在分析", parsed.ReasoningText);
        Assert.AreEqual("call_new", parsed.ToolCalls.Single().Id);
        Assert.AreEqual(3, ((OpenAiResponsesProtocolState)parsed.ProtocolState!).OutputItems.Count);
    }

    [TestMethod]
    public async Task AnthropicRequestAndResponseUseContentBlocks()
    {
        var adapter = new AnthropicMessagesAdapter();
        var request = CreateRequest(AiProtocol.AnthropicMessages, stream: false) with
        {
            Messages =
            [
                AgentMessage.User("查询"),
                AgentMessage.Assistant(
                    "",
                    [CreateToolCall("toolu_1")],
                    "先分析查询范围",
                    [JsonDocument.Parse("{\"type\":\"thinking\",\"thinking\":\"先分析查询范围\",\"signature\":\"sig_1\"}").RootElement.Clone()]),
                AgentMessage.Tool("toolu_1", "{\"count\":1}"),
            ],
        };
        var profile = CreateProfile(AiProtocol.AnthropicMessages) with
        {
            Authentication = new AiAuthenticationConfiguration
            {
                Kind = AiAuthenticationKind.Header,
                HeaderName = "x-api-key",
                HeaderPrefix = string.Empty,
                CredentialReference = "memory:key",
            },
        };

        using var message = adapter.CreateRequest(request, profile, new CredentialValue("anthropic-secret"));
        var body = await message.Content!.ReadAsStringAsync();
        StringAssert.Contains(body, "tool_use");
        StringAssert.Contains(body, "tool_result");
        StringAssert.Contains(body, "thinking");
        StringAssert.Contains(body, "sig_1");
        Assert.AreEqual("anthropic-secret", message.Headers.GetValues("x-api-key").Single());
        Assert.AreEqual("2023-06-01", message.Headers.GetValues("anthropic-version").Single());

        using var response = JsonResponse("""
            {
              "content":[
                {"type":"thinking","thinking":"需要先读取事项","signature":"sig_2"},
                {"type":"text","text":"需要工具"},
                {"type":"tool_use","id":"toolu_2","name":"diary_query_work_items","input":{"date":"2026-10-08"}}
              ],
              "stop_reason":"tool_use",
              "usage":{"input_tokens":7,"output_tokens":3}
            }
            """);
        var parsed = await adapter.ParseResponseAsync(response, CancellationToken.None);
        Assert.AreEqual("需要工具", parsed.Text);
        Assert.AreEqual("需要先读取事项", parsed.ReasoningText);
        Assert.AreEqual(1, parsed.ReasoningContentBlocks?.Count);
        Assert.AreEqual("toolu_2", parsed.ToolCalls.Single().Id);
        Assert.AreEqual(10, parsed.Usage?.TotalTokens);
    }

    [TestMethod]
    public async Task ChatStreamMergesSplitToolArgumentEvents()
    {
        const string sse = """
            data: {"choices":[{"delta":{"reasoning_content":"正在判断查询条件"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"content":"开"},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"diary_query_work_items","arguments":"{\"date\":"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"2026-10-08\"}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = await CollectAsync(new OpenAiChatCompletionsAdapter().ParseStreamAsync(stream, CancellationToken.None));
        Assert.AreEqual("正在判断查询条件", events.Single(item => item.Kind == AgentStreamEventKind.ReasoningDelta).Text);
        Assert.AreEqual("开", events.Single(item => item.Kind == AgentStreamEventKind.TextDelta).Text);
        Assert.AreEqual(2, events.Count(item => item.Kind == AgentStreamEventKind.ToolArgumentsDelta));
        Assert.AreEqual("call_1", events.Single(item => item.Kind == AgentStreamEventKind.ToolCallCompleted).ToolCallId);
    }

    [TestMethod]
    public async Task ResponsesStreamMapsTypedEventsAndUsage()
    {
        const string sse = """
            event: response.reasoning_summary_text.delta
            data: {"type":"response.reasoning_summary_text.delta","output_index":0,"summary_index":0,"delta":"正在分析"}

            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","call_id":"call_1","name":"diary_query_work_items"}}

            event: response.function_call_arguments.delta
            data: {"type":"response.function_call_arguments.delta","output_index":1,"call_id":"call_1","delta":"{\"date\":\"2026-10-08\"}"}

            event: response.function_call_arguments.done
            data: {"type":"response.function_call_arguments.done","output_index":1,"call_id":"call_1"}

            event: response.completed
            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":8,"output_tokens":2,"total_tokens":10}}}

            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = await CollectAsync(new OpenAiResponsesAdapter().ParseStreamAsync(stream, CancellationToken.None));
        Assert.AreEqual("正在分析", events.Single(item => item.Kind == AgentStreamEventKind.ReasoningDelta).Text);
        Assert.AreEqual("call_1", events.Single(item => item.Kind == AgentStreamEventKind.ToolCallStarted).ToolCallId);
        Assert.AreEqual(10, events.Single(item => item.Kind == AgentStreamEventKind.UsageUpdated).Usage?.TotalTokens);
        Assert.AreEqual("completed", events.Last().FinishReason);
    }

    [TestMethod]
    public async Task AnthropicStreamMapsInputJsonDelta()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":6}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"正在选择工具"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig_stream"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"diary_query_work_items"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"date\":\"2026-10-08\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":3}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var events = await CollectAsync(new AnthropicMessagesAdapter().ParseStreamAsync(stream, CancellationToken.None));
        Assert.AreEqual(
            "正在选择工具",
            string.Concat(events.Where(item => item.Kind == AgentStreamEventKind.ReasoningDelta).Select(item => item.Text)));
        Assert.AreEqual("toolu_1", events.Single(item => item.Kind == AgentStreamEventKind.ToolCallStarted).ToolCallId);
        Assert.AreEqual("{\"date\":\"2026-10-08\"}", events.Single(item => item.Kind == AgentStreamEventKind.ToolArgumentsDelta).Text);
        Assert.AreEqual(9, events.Single(item => item.Kind == AgentStreamEventKind.UsageUpdated).Usage?.TotalTokens);
        var completed = events.Single(item => item.Kind == AgentStreamEventKind.ResponseCompleted);
        Assert.AreEqual("sig_stream", completed.ReasoningContentBlocks?[0].GetProperty("signature").GetString());
    }

    [TestMethod]
    [DataRow(AiProtocol.OpenAiChatCompletions)]
    [DataRow(AiProtocol.OpenAiResponses)]
    [DataRow(AiProtocol.AnthropicMessages)]
    public async Task ForcedAndParallelToolChoiceUsesProtocolSpecificShape(AiProtocol protocol)
    {
        IAiProtocolAdapter adapter = protocol switch
        {
            AiProtocol.OpenAiChatCompletions => new OpenAiChatCompletionsAdapter(),
            AiProtocol.OpenAiResponses => new OpenAiResponsesAdapter(),
            AiProtocol.AnthropicMessages => new AnthropicMessagesAdapter(),
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };
        var request = CreateRequest(protocol, stream: false) with
        {
            RequiredToolName = "diary_query_work_items",
            AllowParallelToolCalls = true,
        };

        using var message = adapter.CreateRequest(request, CreateProfile(protocol), new CredentialValue("secret"));
        using var document = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        var toolChoice = root.GetProperty("tool_choice");

        switch (protocol)
        {
            case AiProtocol.OpenAiChatCompletions:
                Assert.IsTrue(root.GetProperty("parallel_tool_calls").GetBoolean());
                Assert.AreEqual(
                    "diary_query_work_items",
                    toolChoice.GetProperty("function").GetProperty("name").GetString());
                break;
            case AiProtocol.OpenAiResponses:
                Assert.IsTrue(root.GetProperty("parallel_tool_calls").GetBoolean());
                Assert.AreEqual("diary_query_work_items", toolChoice.GetProperty("name").GetString());
                break;
            case AiProtocol.AnthropicMessages:
                Assert.AreEqual("tool", toolChoice.GetProperty("type").GetString());
                Assert.AreEqual("diary_query_work_items", toolChoice.GetProperty("name").GetString());
                Assert.IsFalse(toolChoice.GetProperty("disable_parallel_tool_use").GetBoolean());
                break;
        }
    }

    [TestMethod]
    public void ConnectionValidationRejectsHostChangingOrSensitiveUriParts()
    {
        var profile = CreateProfile(AiProtocol.OpenAiResponses) with
        {
            BaseUri = new Uri("https://user:password@example.test/v1#fragment"),
            RequestPathOverride = "https://other.test/responses",
        };
        var errors = AiConnectionProfileValidator.Validate(profile);
        Assert.IsTrue(errors.Count >= 3);
        Assert.ThrowsExactly<ArgumentException>(() =>
            AiConnectionProfileValidator.ResolveRequestUri(profile, "responses"));
    }

    [TestMethod]
    public async Task HttpErrorsAreNormalizedWithoutCredentialInMessage()
    {
        var adapter = new OpenAiChatCompletionsAdapter();
        using var response = JsonResponse(
            "{\"error\":{\"message\":\"unauthorized\"}}",
            HttpStatusCode.Unauthorized);
        var exception = await Assert.ThrowsExactlyAsync<AiModelException>(async () =>
            await adapter.ParseResponseAsync(response, CancellationToken.None));
        Assert.AreEqual(AiModelErrorCategory.Authentication, exception.Category);
        Assert.IsFalse(exception.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    private static AgentModelRequest CreateRequest(AiProtocol protocol, bool stream) => new(
        "test-model",
        "系统指令",
        [AgentMessage.User("你好")],
        [new AgentToolDefinition("diary_query_work_items", "查询事项", ToolSchema)],
        stream,
        1024);

    private static AgentToolCall CreateToolCall(string id) =>
        new(id, "diary_query_work_items", JsonDocument.Parse("{\"date\":\"2026-10-08\"}").RootElement.Clone());

    private static AiConnectionProfile CreateProfile(AiProtocol protocol) => new()
    {
        Id = "test",
        DisplayName = "Test",
        Protocol = protocol,
        BaseUri = new Uri("https://example.test/v1/"),
        Model = "test-model",
        Authentication = new AiAuthenticationConfiguration
        {
            Kind = AiAuthenticationKind.Bearer,
            CredentialReference = "memory:key",
        },
        Proxy = new AiProxyConfiguration { Mode = AiProxyMode.Direct },
    };

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> source)
    {
        var result = new List<AgentStreamEvent>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }
}
