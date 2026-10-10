using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Diary.Survey;
using Microsoft.Extensions.Logging;

namespace Diary.Mcp.Remote;

internal sealed class McpHttpServer(
    SurveyMcpPeerAccessPolicy peerPolicy,
    PeerRequestRateLimiter rateLimiter,
    RemoteDiaryTools tools,
    ILogger logger) : IAsyncDisposable
{
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private Task? _acceptLoop;
    private long _connectionId;

    public int Port { get; private set; }

    public void Start()
    {
        if (_listener is not null)
            return;
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _shutdown = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _shutdown.Token));
    }

    public async Task StopAsync()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        var shutdown = Interlocked.Exchange(ref _shutdown, null);
        var acceptLoop = Interlocked.Exchange(ref _acceptLoop, null);
        shutdown?.Cancel();
        listener?.Stop();
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown?.IsCancellationRequested == true)
            {
            }
            catch (SocketException) when (shutdown?.IsCancellationRequested == true)
            {
            }
        }

        var connections = _connections.Values.ToArray();
        if (connections.Length > 0)
        {
            try
            {
                await Task.WhenAll(connections).ConfigureAwait(false);
            }
            catch when (shutdown?.IsCancellationRequested == true)
            {
            }
        }
        shutdown?.Dispose();
        Port = 0;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var id = Interlocked.Increment(ref _connectionId);
            var task = HandleClientAsync(client, cancellationToken);
            _connections[id] = task;
            _ = task.ContinueWith(
                completedTask => _connections.TryRemove(id, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            await using var stream = client.GetStream();
            if (!peerPolicy.IsAllowed(remoteAddress))
            {
                logger.LogWarning(
                    "拒绝未授权来源访问远程 MCP 服务。RemoteAddress={RemoteAddress}",
                    remoteAddress);
                await WriteResponseAsync(stream, 403, "Forbidden", null, cancellationToken);
                return;
            }
            var retryAfter = TimeSpan.Zero;
            if (remoteAddress is null
                || !rateLimiter.TryAcquire(remoteAddress, DateTimeOffset.UtcNow, out retryAfter))
            {
                await WriteResponseAsync(
                    stream,
                    429,
                    "Too Many Requests",
                    null,
                    cancellationToken,
                    new Dictionary<string, string>
                    {
                        ["Retry-After"] = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(),
                    });
                return;
            }

            HttpRequestData request;
            try
            {
                request = await ReadRequestAsync(stream, cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                logger.LogDebug(exception, "远程 MCP HTTP 请求无效。");
                await WriteResponseAsync(stream, 400, "Bad Request", null, cancellationToken);
                return;
            }

            if (!string.Equals(request.Path, "/mcp", StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, 404, "Not Found", null, cancellationToken);
                return;
            }
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, 405, "Method Not Allowed", null, cancellationToken);
                return;
            }

            await HandleJsonRpcAsync(stream, request.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleJsonRpcAsync(
        NetworkStream stream,
        byte[] body,
        CancellationToken cancellationToken)
    {
        JsonElement? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString()
                ?? throw new JsonException("method 不能为空。");
            var hasId = root.TryGetProperty("id", out var id);
            if (hasId)
                requestId = id.Clone();
            var parameters = root.TryGetProperty("params", out var value)
                ? value
                : default;

            if (!hasId)
            {
                await WriteResponseAsync(stream, 202, "Accepted", null, cancellationToken);
                return;
            }

            object result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "DiaryApp Remote MCP", version = "1.0.0" },
                },
                "tools/list" => new
                {
                    tools = RemoteDiaryTools.Descriptors.Select(descriptor => new
                    {
                        name = descriptor.Name,
                        title = descriptor.Title,
                        description = descriptor.Description,
                        inputSchema = descriptor.InputSchema,
                        annotations = new
                        {
                            readOnlyHint = descriptor.Annotations.ReadOnlyHint,
                            destructiveHint = descriptor.Annotations.DestructiveHint,
                            idempotentHint = descriptor.Annotations.IdempotentHint,
                            openWorldHint = descriptor.Annotations.OpenWorldHint,
                        },
                    }),
                },
                "tools/call" => await CallToolAsync(parameters, cancellationToken).ConfigureAwait(false),
                _ => throw new McpMethodNotFoundException(method),
            };
            await WriteJsonRpcResultAsync(stream, requestId!.Value, result, cancellationToken).ConfigureAwait(false);
        }
        catch (McpMethodNotFoundException exception)
        {
            await WriteJsonRpcErrorAsync(stream, requestId, -32601, exception.Message, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogDebug(exception, "远程 MCP JSON-RPC 请求失败。");
            await WriteJsonRpcErrorAsync(stream, requestId, -32602, exception.Message, cancellationToken);
        }
    }

    private async Task<object> CallToolAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var name = parameters.GetProperty("name").GetString()
            ?? throw new JsonException("工具名称不能为空。");
        var arguments = parameters.TryGetProperty("arguments", out var value)
            ? value
            : EmptyObject();
        try
        {
            var text = await tools.CallAsync(name, arguments, cancellationToken).ConfigureAwait(false);
            return new { content = new[] { new { type = "text", text } }, isError = false };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "远程 MCP 只读工具调用失败。ToolName={ToolName}", name);
            return new
            {
                content = new[] { new { type = "text", text = "只读工具调用失败。" } },
                isError = true,
            };
        }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static async Task<HttpRequestData> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        var buffer = new byte[4096];
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new InvalidDataException("HTTP 请求头未完成。");
            received.Write(buffer, 0, count);
            if (received.Length > MaxHeaderBytes + MaxBodyBytes)
                throw new InvalidDataException("HTTP 请求过大。");
            headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
            if (headerEnd < 0 && received.Length > MaxHeaderBytes)
                throw new InvalidDataException("HTTP 请求头过大。");
        }

        var bytes = received.GetBuffer();
        var headerText = Encoding.ASCII.GetString(bytes, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
            throw new InvalidDataException("HTTP 请求行无效。");
        var headers = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        if (!headers.TryGetValue("Content-Length", out var contentLengthText)
            || !int.TryParse(contentLengthText, out var contentLength)
            || contentLength is < 0 or > MaxBodyBytes)
        {
            throw new InvalidDataException("Content-Length 无效。");
        }

        var bodyOffset = headerEnd + 4;
        while (received.Length - bodyOffset < contentLength)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new InvalidDataException("HTTP 请求体未完成。");
            received.Write(buffer, 0, count);
        }
        return new HttpRequestData(
            requestLine[0],
            requestLine[1].Split('?', 2)[0],
            bytes.AsSpan(bodyOffset, contentLength).ToArray());
    }

    private static int FindHeaderEnd(byte[] bytes, int length)
    {
        for (var index = 0; index <= length - 4; index++)
        {
            if (bytes[index] == '\r' && bytes[index + 1] == '\n'
                && bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
            {
                return index;
            }
        }
        return -1;
    }

    private static Task WriteJsonRpcResultAsync(
        NetworkStream stream,
        JsonElement id,
        object result,
        CancellationToken cancellationToken) => WriteResponseAsync(
        stream,
        200,
        "OK",
        JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, result }),
        cancellationToken);

    private static Task WriteJsonRpcErrorAsync(
        NetworkStream stream,
        JsonElement? id,
        int code,
        string message,
        CancellationToken cancellationToken) => WriteResponseAsync(
        stream,
        200,
        "OK",
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message },
        }),
        cancellationToken);

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        byte[]? body,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        body ??= [];
        var headers = new StringBuilder()
            .Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Connection: close\r\n");
        if (body.Length > 0)
            headers.Append("Content-Type: application/json; charset=utf-8\r\n");
        if (extraHeaders is not null)
        {
            foreach (var header in extraHeaders)
                headers.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }
        headers.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private sealed record HttpRequestData(string Method, string Path, byte[] Body);

    private sealed class McpMethodNotFoundException(string method)
        : Exception($"不支持的 MCP 方法：{method}。");
}
