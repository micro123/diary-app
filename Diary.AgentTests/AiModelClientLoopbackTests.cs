using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Networking;
using Diary.Agent.Protocols;

namespace Diary.AgentTests;

[TestClass]
public sealed class AiModelClientLoopbackTests
{
    [TestMethod]
    public async Task ResponsesClientSendsAuthenticatedStoreFalseRequestToConfiguredPath()
    {
        await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(
            "application/json",
            """
            {"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}
            """));
        var credentials = new TestCredentialStore("loopback-secret");
        using var pool = new AiHttpClientPool(credentials);
        var client = CreateClient(credentials, pool);
        var profile = CreateProfile(server.BaseUri, AiProtocol.OpenAiResponses);

        var response = await client.SendAsync(CreateRequest(stream: false), profile);
        var captured = await server.RequestTask;

        Assert.AreEqual("ok", response.Text);
        Assert.AreEqual("/v1/responses", captured.Path);
        Assert.AreEqual("Bearer loopback-secret", captured.Headers["Authorization"]);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.IsFalse(body.RootElement.GetProperty("store").GetBoolean());
        Assert.IsFalse(body.RootElement.TryGetProperty("previous_response_id", out _));
    }

    [TestMethod]
    public async Task AnthropicClientStreamsSseOverRealHttpConnection()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":2}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        await using var server = new LoopbackHttpServer(_ => new LoopbackResponse("text/event-stream", sse));
        var credentials = new TestCredentialStore("anthropic-key");
        using var pool = new AiHttpClientPool(credentials);
        var client = CreateClient(credentials, pool);
        var profile = CreateProfile(server.BaseUri, AiProtocol.AnthropicMessages) with
        {
            Authentication = new AiAuthenticationConfiguration
            {
                Kind = AiAuthenticationKind.Header,
                HeaderName = "x-api-key",
                HeaderPrefix = string.Empty,
                CredentialReference = "memory:test",
            },
        };
        var events = new List<AgentStreamEvent>();

        await foreach (var item in client.StreamAsync(CreateRequest(stream: true), profile))
            events.Add(item);
        var captured = await server.RequestTask;

        Assert.AreEqual("hello", events.Single(item => item.Kind == AgentStreamEventKind.TextDelta).Text);
        Assert.AreEqual(3, events.Single(item => item.Kind == AgentStreamEventKind.UsageUpdated).Usage?.TotalTokens);
        Assert.AreEqual("anthropic-key", captured.Headers["x-api-key"]);
        Assert.AreEqual("2023-06-01", captured.Headers["anthropic-version"]);
    }

    [TestMethod]
    public async Task StreamingIdleTimeoutRenewsWhenEventsContinueArriving()
    {
        var chunks = new[] { "one\n", "two\n", "three\n", "four\n" };
        await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(
            "text/event-stream",
            string.Concat(chunks),
            Chunks: chunks,
            ChunkDelay: TimeSpan.FromMilliseconds(200)));
        var credentials = new TestCredentialStore("key");
        using var pool = new AiHttpClientPool(credentials);
        var client = new AiModelClient(credentials, pool, [new TestStreamingAdapter()]);
        var profile = CreateProfile(server.BaseUri, AiProtocol.OpenAiResponses) with
        {
            RequestTimeout = TimeSpan.FromMilliseconds(350),
        };
        var events = new List<AgentStreamEvent>();

        await foreach (var item in client.StreamAsync(CreateRequest(stream: true), profile))
            events.Add(item);

        Assert.IsTrue(events.Any(item => item.Kind == AgentStreamEventKind.ResponseCompleted));
    }

    [TestMethod]
    public async Task StreamingIdleTimeoutIsReportedAsRequestTimeout()
    {
        await using var server = new LoopbackHttpServer(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var credentials = new TestCredentialStore("key");
        using var pool = new AiHttpClientPool(credentials);
        var client = new AiModelClient(credentials, pool, [new TestStreamingAdapter()]);
        var profile = CreateProfile(server.BaseUri, AiProtocol.OpenAiResponses) with
        {
            RequestTimeout = TimeSpan.FromMilliseconds(100),
        };

        var exception = await Assert.ThrowsExactlyAsync<AiModelException>(async () =>
        {
            await foreach (var _ in client.StreamAsync(CreateRequest(stream: true), profile))
            {
            }
        });

        Assert.AreEqual(AiModelErrorCategory.Timeout, exception.Category);
        Assert.AreEqual("request_timeout", exception.Code);
    }

    [TestMethod]
    public async Task CallerCancellationStopsRealHttpRequest()
    {
        await using var server = new LoopbackHttpServer(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return new LoopbackResponse("application/json", "{}");
        });
        var credentials = new TestCredentialStore("key");
        using var pool = new AiHttpClientPool(credentials);
        var client = CreateClient(credentials, pool);
        var profile = CreateProfile(server.BaseUri, AiProtocol.OpenAiChatCompletions);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsExactlyAsync<AiModelException>(async () =>
            await client.SendAsync(CreateRequest(stream: false), profile, cancellation.Token));

        Assert.AreEqual(AiModelErrorCategory.Cancelled, exception.Category);
        Assert.AreEqual("request_cancelled", exception.Code);
    }

    [TestMethod]
    public void OfflineCertificateRevocationIsReportedWithActionableTlsError()
    {
        var networkException = new HttpRequestException(
            "The SSL connection could not be established.",
            new AuthenticationException(
                "The remote certificate is invalid because of errors in the certificate chain: RevocationStatusUnknown, OfflineRevocation"));

        var exception = AiModelClient.NormalizeNetworkException(networkException);

        Assert.AreEqual(AiModelErrorCategory.Tls, exception.Category);
        Assert.AreEqual("tls_revocation_offline", exception.Code);
        StringAssert.Contains(exception.Message, "检查证书吊销状态");
    }

    [TestMethod]
    public void OtherAuthenticationFailuresRemainTlsErrors()
    {
        var networkException = new HttpRequestException(
            "The SSL connection could not be established.",
            new AuthenticationException("RemoteCertificateNameMismatch"));

        var exception = AiModelClient.NormalizeNetworkException(networkException);

        Assert.AreEqual(AiModelErrorCategory.Tls, exception.Category);
        Assert.AreEqual("tls_error", exception.Code);
    }

    [TestMethod]
    public async Task CertificateRevocationPolicyUsesSeparatePooledClients()
    {
        var credentials = new TestCredentialStore("key");
        using var pool = new AiHttpClientPool(credentials);
        var profile = CreateProfile(new Uri("https://models.example.test/"), AiProtocol.OpenAiResponses);

        var checkedClient = await pool.GetClientAsync(profile);
        var uncheckedClient = await pool.GetClientAsync(profile with { CheckCertificateRevocation = false });

        Assert.AreNotSame(checkedClient, uncheckedClient);
    }

    [TestMethod]
    public async Task AdditionalLiteralAndCredentialHeadersAreSentWithoutStoringSecretValue()
    {
        await using var server = new LoopbackHttpServer(_ => new LoopbackResponse(
            "application/json",
            """
            {"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}
            """));
        var credentials = new TestCredentialStore("header-secret");
        using var pool = new AiHttpClientPool(credentials);
        var client = CreateClient(credentials, pool);
        var profile = CreateProfile(server.BaseUri, AiProtocol.OpenAiResponses) with
        {
            Authentication = new AiAuthenticationConfiguration { Kind = AiAuthenticationKind.None },
            Headers =
            [
                new AiRequestHeader { Name = "X-Tenant", Value = "diary" },
                new AiRequestHeader
                {
                    Name = "X-Internal-Token",
                    CredentialReference = "memory:header",
                    Prefix = "Token ",
                },
            ],
        };

        _ = await client.SendAsync(CreateRequest(stream: false), profile);
        var captured = await server.RequestTask;

        Assert.AreEqual("diary", captured.Headers["X-Tenant"]);
        Assert.AreEqual("Token header-secret", captured.Headers["X-Internal-Token"]);
        Assert.IsFalse(JsonSerializer.Serialize(profile).Contains("header-secret", StringComparison.Ordinal));
    }

    private static AiModelClient CreateClient(IAiCredentialStore credentials, AiHttpClientPool pool) => new(
        credentials,
        pool,
        [
            new OpenAiChatCompletionsAdapter(),
            new OpenAiResponsesAdapter(),
            new AnthropicMessagesAdapter(),
        ]);

    private static AgentModelRequest CreateRequest(bool stream) => new(
        "test-model",
        "system",
        [AgentMessage.User("hello")],
        [],
        stream,
        128);

    private static AiConnectionProfile CreateProfile(Uri baseUri, AiProtocol protocol) => new()
    {
        Id = "loopback",
        DisplayName = "Loopback",
        Protocol = protocol,
        BaseUri = new Uri(baseUri, "v1/"),
        Model = "test-model",
        Authentication = new AiAuthenticationConfiguration
        {
            Kind = AiAuthenticationKind.Bearer,
            CredentialReference = "memory:test",
        },
        Proxy = new AiProxyConfiguration { Mode = AiProxyMode.Direct },
    };

    private sealed class TestCredentialStore(string value) : IAiCredentialStore
    {
        public ValueTask<bool> ExistsAsync(string reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask<CredentialValue?> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CredentialValue?>(new CredentialValue(value));

        public ValueTask SetAsync(
            string reference,
            ReadOnlyMemory<char> credential,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(string reference, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class TestStreamingAdapter : IAiProtocolAdapter
    {
        public AiProtocol Protocol => AiProtocol.OpenAiResponses;

        public string DefaultRequestPath => "stream";

        public HttpRequestMessage CreateRequest(
            AgentModelRequest request,
            AiConnectionProfile connection,
            CredentialValue? credential) => new(HttpMethod.Post, new Uri(connection.BaseUri, DefaultRequestPath));

        public ValueTask<AgentModelResponse> ParseResponseAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<AgentStreamEvent> ParseStreamAsync(
            Stream stream,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                yield return new AgentStreamEvent(AgentStreamEventKind.TextDelta, Text: line);
            yield return new AgentStreamEvent(AgentStreamEventKind.ResponseCompleted, FinishReason: "stop");
        }
    }

    private sealed record CapturedRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

    private sealed record LoopbackResponse(
        string ContentType,
        string Body,
        HttpStatusCode Status = HttpStatusCode.OK,
        IReadOnlyList<string>? Chunks = null,
        TimeSpan? ChunkDelay = null);

    private sealed class LoopbackHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Func<CapturedRequest, CancellationToken, Task<LoopbackResponse>> _handler;

        public LoopbackHttpServer(Func<CapturedRequest, LoopbackResponse> handler)
            : this((request, _) => Task.FromResult(handler(request)))
        {
        }

        public LoopbackHttpServer(Func<CapturedRequest, CancellationToken, Task<LoopbackResponse>> handler)
        {
            _handler = handler;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/");
            RequestTask = ServeOneAsync();
        }

        public Uri BaseUri { get; }

        public Task<CapturedRequest> RequestTask { get; }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try
            {
                await RequestTask;
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
            _shutdown.Dispose();
        }

        private async Task<CapturedRequest> ServeOneAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, _shutdown.Token);
            var response = await _handler(request, _shutdown.Token);
            var body = Encoding.UTF8.GetBytes(response.Body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(int)response.Status} {response.Status}\r\n" +
                $"Content-Type: {response.ContentType}; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, _shutdown.Token);
            if (response.Chunks is null)
            {
                await stream.WriteAsync(body, _shutdown.Token);
            }
            else
            {
                for (var index = 0; index < response.Chunks.Count; index++)
                {
                    if (index > 0 && response.ChunkDelay is { } delay)
                        await Task.Delay(delay, _shutdown.Token);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response.Chunks[index]), _shutdown.Token);
                    await stream.FlushAsync(_shutdown.Token);
                }
            }
            await stream.FlushAsync(_shutdown.Token);
            return request;
        }

        private static async Task<CapturedRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
        {
            var headerBytes = new List<byte>();
            var endState = 0;
            while (endState < 4)
            {
                var buffer = new byte[1];
                if (await stream.ReadAsync(buffer, cancellationToken) == 0)
                    throw new EndOfStreamException();
                headerBytes.Add(buffer[0]);
                endState = (endState, buffer[0]) switch
                {
                    (0, (byte)'\r') => 1,
                    (1, (byte)'\n') => 2,
                    (2, (byte)'\r') => 3,
                    (3, (byte)'\n') => 4,
                    (_, (byte)'\r') => 1,
                    _ => 0,
                };
            }
            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Skip(1)
                .Select(line => line.Split(':', 2))
                .ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var contentLength = headers.TryGetValue("Content-Length", out var contentLengthText)
                ? int.Parse(contentLengthText, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            var bodyBytes = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(bodyBytes.AsMemory(offset), cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException();
                offset += read;
            }
            return new CapturedRequest(
                requestLine[0],
                requestLine[1],
                headers,
                Encoding.UTF8.GetString(bodyBytes));
        }
    }
}
