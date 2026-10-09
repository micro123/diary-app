using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Mcp;
using Diary.Agent.Tools;
using Diary.AiContext;

namespace Diary.AgentTests;

[TestClass]
public sealed class McpClientTests
{
    [TestMethod]
    [DataRow("delete_work_item")]
    [DataRow("remove-work-item")]
    [DataRow("purgeCache")]
    [DataRow("filesystem_unlink")]
    public void EnabledDeleteSemanticToolPolicyIsRejected(string toolName)
    {
        var errors = McpServerProfileValidator.Validate(CreatePolicyValidationProfile(
            new McpToolPolicy
            {
                ToolName = toolName,
                Enabled = true,
                Risk = AgentToolRisk.ReadOnly,
            }));

        Assert.IsTrue(errors.Any(error => error.Contains("删除语义", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DisabledDeleteSemanticToolPolicyCanRemainInSettings()
    {
        var errors = McpServerProfileValidator.Validate(CreatePolicyValidationProfile(
            new McpToolPolicy
            {
                ToolName = "delete_work_item",
                Enabled = false,
                Risk = AgentToolRisk.ReadOnly,
            }));

        Assert.IsFalse(errors.Any(error => error.Contains("删除语义", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NonDeleteWriteToolPolicyRemainsSupported()
    {
        var errors = McpServerProfileValidator.Validate(CreatePolicyValidationProfile(
            new McpToolPolicy
            {
                ToolName = "update_work_item",
                Enabled = true,
                Risk = AgentToolRisk.Write,
            }));

        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task StdioClientInitializesListsAndCallsRealDiaryMcpServer()
    {
        var root = FindRepositoryRoot();
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"diary-agent-mcp-{Guid.NewGuid():N}.json");
        await AiContextSerializer.SaveAsync(snapshotPath, CreateSnapshot());
        try
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var serverPath = Path.Combine(root, "Diary.Mcp", "bin", configuration, "net10.0", "Diary.Mcp.dll");
            Assert.IsTrue(File.Exists(serverPath), $"找不到 MCP Server：{serverPath}");
            var profile = new McpServerProfile
            {
                Id = "diary-readonly",
                DisplayName = "Diary MCP",
                Enabled = true,
                Transport = McpTransportKind.Stdio,
                Command = "dotnet",
                Arguments = [serverPath, "--snapshot", snapshotPath],
                WorkingDirectory = root,
            };
            await using var connection = new McpClientConnection(profile, new TestCredentialStore());

            var tools = await connection.ListToolsAsync();
            var result = await connection.CallToolAsync("diary_list_tags", JsonSerializer.SerializeToElement(new { }));

            CollectionAssert.Contains(tools.Select(tool => tool.Name).ToArray(), "diary_list_tags");
            Assert.IsFalse(result.IsError);
            StringAssert.Contains(result.Result.GetRawText(), "work");
            Assert.IsFalse(result.Result.GetRawText().Contains("local-note-secret", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(snapshotPath);
        }
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task StreamableHttpUsesSessionAuthSseNotificationAndToolCall()
    {
        await using var server = new StreamableMcpServer();
        var credentials = new TestCredentialStore(new Dictionary<string, string>
        {
            ["memory:mcp-token"] = "mcp-secret",
        });
        var profile = new McpServerProfile
        {
            Id = "http-test",
            DisplayName = "HTTP Test",
            Enabled = true,
            Transport = McpTransportKind.StreamableHttp,
            Endpoint = server.Endpoint,
            Proxy = new AiProxyConfiguration { Mode = AiProxyMode.Direct },
            Authentication = new AiAuthenticationConfiguration
            {
                Kind = AiAuthenticationKind.Bearer,
                CredentialReference = "memory:mcp-token",
            },
        };
        await using var connection = new McpClientConnection(profile, credentials);
        var listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ToolsChanged += _ =>
        {
            listChanged.TrySetResult();
            return ValueTask.CompletedTask;
        };

        var tools = await connection.ListToolsAsync();
        var call = await connection.CallToolAsync("echo", JsonSerializer.SerializeToElement(new { value = "hello" }));
        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("echo", tools.Single().Name);
        Assert.IsFalse(call.IsError);
        StringAssert.Contains(call.Result.GetRawText(), "hello");
        await server.WaitForRequestsAsync(5);
        Assert.IsTrue(server.Requests.All(request => request.Headers.TryGetValue("Authorization", out var value)
                                                     && value == "Bearer mcp-secret"));
        Assert.IsTrue(server.Requests.Skip(1).All(request => request.Headers.TryGetValue("Mcp-Session-Id", out var value)
                                                        && value == "session-1"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DiaryApp.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 DiaryApp.sln。");
    }

    private static McpServerProfile CreatePolicyValidationProfile(McpToolPolicy policy) => new()
    {
        Id = "policy-test",
        DisplayName = "Policy Test",
        Enabled = true,
        Transport = McpTransportKind.Stdio,
        Command = "test-mcp-server",
        Tools = [policy],
    };

    private static AiContextSnapshot CreateSnapshot() => new()
    {
        Disclosure = new AiContextDisclosure(true, true, true, true, false, true, true),
        Tags = [new AiContextTag(1, "work", 0, "Primary", false)],
        WorkItems =
        [
            new AiContextWorkItem(1, "2026-10-08", "first", 1, 0, null, [1], []),
        ],
        Audit = new AiContextAudit(["tags", "work_items"], 1, 0, 0, 0, 0, 1, 0),
    };

    private sealed class TestCredentialStore(IReadOnlyDictionary<string, string>? values = null) : IAiCredentialStore
    {
        private readonly IReadOnlyDictionary<string, string> _values = values ?? new Dictionary<string, string>();

        public ValueTask<bool> ExistsAsync(string reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.ContainsKey(reference));

        public ValueTask<CredentialValue?> GetAsync(string reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_values.TryGetValue(reference, out var value) ? new CredentialValue(value) : null);

        public ValueTask SetAsync(string reference, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record CapturedHttpRequest(
        string Method,
        IReadOnlyDictionary<string, string> Headers,
        string Body);

    private sealed class StreamableMcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serveTask;
        private readonly List<CapturedHttpRequest> _requests = [];

        public StreamableMcpServer()
        {
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            _serveTask = ServeAsync();
        }

        public Uri Endpoint { get; }

        public IReadOnlyList<CapturedHttpRequest> Requests
        {
            get
            {
                lock (_requests)
                    return _requests.ToArray();
            }
        }

        public async Task WaitForRequestsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Requests.Count < count)
                await Task.Delay(20, timeout.Token);
        }

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
            var handlers = new List<Task>();
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                    handlers.Add(HandleAsync(client, _shutdown.Token));
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                await Task.WhenAll(handlers);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var request = await ReadRequestAsync(stream, cancellationToken);
                lock (_requests)
                    _requests.Add(request);
                if (request.Method == "GET")
                {
                    const string sse = "event: message\r\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\",\"params\":{}}\r\n\r\n";
                    await WriteResponseAsync(stream, "200 OK", "text/event-stream", sse, cancellationToken, session: false);
                    return;
                }
                using var document = JsonDocument.Parse(request.Body);
                var root = document.RootElement;
                var method = root.GetProperty("method").GetString();
                if (!root.TryGetProperty("id", out var id))
                {
                    await WriteResponseAsync(stream, "202 Accepted", "application/json", string.Empty, cancellationToken);
                    return;
                }
                object result = method switch
                {
                    "initialize" => new
                    {
                        protocolVersion = "2025-11-25",
                        capabilities = new { tools = new { listChanged = true } },
                        serverInfo = new { name = "test", version = "1" },
                    },
                    "tools/list" => new
                    {
                        tools = new[]
                        {
                            new
                            {
                                name = "echo",
                                description = "echo",
                                inputSchema = new { type = "object" },
                            },
                        },
                    },
                    "tools/call" => new
                    {
                        content = new[] { new { type = "text", text = "hello" } },
                        isError = false,
                    },
                    _ => new { },
                };
                var response = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id.GetInt64(),
                    result,
                });
                await WriteResponseAsync(stream, "200 OK", "application/json", response, cancellationToken);
            }
        }

        private static async Task<CapturedHttpRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
        {
            var headerBytes = new List<byte>();
            var state = 0;
            while (state < 4)
            {
                var one = new byte[1];
                if (await stream.ReadAsync(one, cancellationToken) == 0)
                    throw new EndOfStreamException();
                headerBytes.Add(one[0]);
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
            var lines = Encoding.ASCII.GetString(headerBytes.ToArray())
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var method = lines[0].Split(' ')[0];
            var headers = lines.Skip(1)
                .Select(line => line.Split(':', 2))
                .ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var length = headers.TryGetValue("Content-Length", out var text) ? int.Parse(text) : 0;
            var body = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException();
                offset += read;
            }
            return new CapturedHttpRequest(method, headers, Encoding.UTF8.GetString(body));
        }

        private static async Task WriteResponseAsync(
            Stream stream,
            string status,
            string contentType,
            string bodyText,
            CancellationToken cancellationToken,
            bool session = true)
        {
            var body = Encoding.UTF8.GetBytes(bodyText);
            var sessionHeader = session ? "Mcp-Session-Id: session-1\r\n" : string.Empty;
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n{sessionHeader}Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            if (body.Length > 0)
                await stream.WriteAsync(body, cancellationToken);
        }
    }
}
