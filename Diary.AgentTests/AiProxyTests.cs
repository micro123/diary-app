using System.Net;
using System.Net.Sockets;
using System.Text;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Networking;
using Diary.Agent.Protocols;

namespace Diary.AgentTests;

[TestClass]
public sealed class AiProxyTests
{
    [TestMethod]
    [Timeout(15_000)]
    public async Task InheritedCustomProxyRespondsToBasicChallengeWithStoredCredentials()
    {
        await using var proxy = new ChallengingProxyServer(allowAuthenticatedRequest: true);
        var credentials = new DictionaryCredentialStore(new Dictionary<string, string>
        {
            ["memory:proxy-user"] = "alice",
            ["memory:proxy-password"] = "wonderland",
        });
        var defaultProxy = CreateProxy(proxy.Endpoint);
        using var pool = new AiHttpClientPool(credentials, () => defaultProxy);
        var client = CreateClient(credentials, pool);
        var profile = CreateProfile() with
        {
            Proxy = new AiProxyConfiguration { Mode = AiProxyMode.Inherit },
        };

        var response = await client.SendAsync(CreateRequest(), profile);
        var authenticated = await proxy.AuthenticatedRequest;

        Assert.AreEqual("ok", response.Text);
        StringAssert.StartsWith(authenticated.Headers["Proxy-Authorization"], "Basic ");
        Assert.IsFalse(authenticated.Headers["Proxy-Authorization"].Contains("wonderland", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task Proxy407WithoutCredentialsIsClassifiedAsProxyFailure()
    {
        await using var proxy = new ChallengingProxyServer(allowAuthenticatedRequest: false);
        var credentials = new DictionaryCredentialStore(new Dictionary<string, string>());
        using var pool = new AiHttpClientPool(credentials);
        var client = CreateClient(credentials, pool);
        var profile = CreateProfile() with
        {
            Proxy = new AiProxyConfiguration
            {
                Mode = AiProxyMode.Custom,
                Address = proxy.Endpoint,
            },
        };

        var exception = await Assert.ThrowsExactlyAsync<AiModelException>(async () =>
            await client.SendAsync(CreateRequest(), profile));

        Assert.AreEqual(AiModelErrorCategory.Proxy, exception.Category);
        Assert.AreEqual(407, exception.StatusCode);
    }

    private static AiProxyConfiguration CreateProxy(Uri endpoint) => new()
    {
        Mode = AiProxyMode.Custom,
        Address = endpoint,
        UsernameCredentialReference = "memory:proxy-user",
        PasswordCredentialReference = "memory:proxy-password",
    };

    private static AiConnectionProfile CreateProfile() => new()
    {
        Id = "proxy-test",
        DisplayName = "Proxy Test",
        Protocol = AiProtocol.OpenAiResponses,
        BaseUri = new Uri("http://model.example.invalid/v1/"),
        Model = "test",
        Authentication = new AiAuthenticationConfiguration { Kind = AiAuthenticationKind.None },
        RequestTimeout = TimeSpan.FromSeconds(5),
        ConnectTimeout = TimeSpan.FromSeconds(2),
    };

    private static AgentModelRequest CreateRequest() => new(
        "test",
        "system",
        [AgentMessage.User("hello")],
        [],
        false,
        32);

    private static AiModelClient CreateClient(IAiCredentialStore credentials, AiHttpClientPool pool) => new(
        credentials,
        pool,
        [new OpenAiResponsesAdapter()]);

    private sealed class DictionaryCredentialStore(IReadOnlyDictionary<string, string> values) : IAiCredentialStore
    {
        public ValueTask<bool> ExistsAsync(string reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(values.ContainsKey(reference));

        public ValueTask<CredentialValue?> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(values.TryGetValue(reference, out var value) ? new CredentialValue(value) : null);

        public ValueTask SetAsync(string reference, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record ProxyRequest(IReadOnlyDictionary<string, string> Headers);

    private sealed class ChallengingProxyServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly bool _allowAuthenticatedRequest;
        private readonly Task _serveTask;
        private readonly TaskCompletionSource<ProxyRequest> _authenticated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ChallengingProxyServer(bool allowAuthenticatedRequest)
        {
            _allowAuthenticatedRequest = allowAuthenticatedRequest;
            _listener.Start();
            Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _serveTask = ServeAsync();
        }

        public Uri Endpoint { get; }

        public Task<ProxyRequest> AuthenticatedRequest => _authenticated.Task;

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try { await _serveTask; }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
            _shutdown.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                for (var attempt = 0; attempt < 3 && !_shutdown.IsCancellationRequested; attempt++)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                    await using var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, _shutdown.Token);
                    if (_allowAuthenticatedRequest && request.Headers.ContainsKey("Proxy-Authorization"))
                    {
                        _authenticated.TrySetResult(request);
                        await WriteAsync(
                            stream,
                            "200 OK",
                            "application/json",
                            """
                            {"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}
                            """,
                            null,
                            _shutdown.Token);
                        return;
                    }
                    await WriteAsync(
                        stream,
                        "407 Proxy Authentication Required",
                        "text/plain",
                        string.Empty,
                        "Proxy-Authenticate: Basic realm=\"DiaryTests\"\r\n",
                        _shutdown.Token);
                    if (!_allowAuthenticatedRequest)
                        return;
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
        }

        private static async Task<ProxyRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            var state = 0;
            while (state < 4)
            {
                var one = new byte[1];
                if (await stream.ReadAsync(one, cancellationToken) == 0)
                    throw new EndOfStreamException();
                bytes.Add(one[0]);
                state = (state, one[0]) switch
                {
                    (0, (byte)'\r') => 1,
                    (1, (byte)'\n') => 2,
                    (2, (byte)'\r') => 3,
                    (3, (byte)'\n') => 4,
                    (_, (byte)'\r') => 1,
                    _ => 0,
                };
            }
            var lines = Encoding.ASCII.GetString(bytes.ToArray())
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Skip(1)
                .Select(line => line.Split(':', 2))
                .ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var contentLength = headers.TryGetValue("Content-Length", out var contentLengthText)
                ? int.Parse(contentLengthText, System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            var body = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException();
                offset += read;
            }
            return new ProxyRequest(headers);
        }

        private static async Task WriteAsync(
            Stream stream,
            string status,
            string contentType,
            string bodyText,
            string? extraHeaders,
            CancellationToken cancellationToken)
        {
            var body = Encoding.UTF8.GetBytes(bodyText);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n{extraHeaders}Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            if (body.Length > 0)
                await stream.WriteAsync(body, cancellationToken);
        }
    }
}
