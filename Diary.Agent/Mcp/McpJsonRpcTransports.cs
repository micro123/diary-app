using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;

namespace Diary.Agent.Mcp;

internal interface IMcpJsonRpcTransport : IAsyncDisposable
{
    event Func<string, JsonElement, CancellationToken, ValueTask>? NotificationReceived;

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask<JsonElement> RequestAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default);

    ValueTask NotifyAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default);

    ValueTask StartNotificationPumpAsync(CancellationToken cancellationToken = default);
}

internal sealed class McpTransportException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class StdioMcpTransport(McpServerProfile profile) : IMcpJsonRpcTransport
{
    private const int MaxMessageCharacters = 4 * 1024 * 1024;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Process? _process;
    private Task? _readLoop;
    private Task? _errorLoop;
    private long _nextId;

    public event Func<string, JsonElement, CancellationToken, ValueTask>? NotificationReceived;

    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
            return ValueTask.CompletedTask;
        var startInfo = new ProcessStartInfo
        {
            FileName = profile.Command,
            WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(profile.WorkingDirectory),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in profile.Arguments)
            startInfo.ArgumentList.Add(argument);
        _process = Process.Start(startInfo)
            ?? throw new McpTransportException($"无法启动 MCP Server：{profile.DisplayName}。");
        _readLoop = ReadLoopAsync(_shutdown.Token);
        _errorLoop = DrainStandardErrorAsync(_shutdown.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<JsonElement> RequestAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("MCP JSON-RPC ID 冲突。");
        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetCanceled(cancellationToken);
        });
        try
        {
            await WriteAsync(CreateMessage(id, method, parameters), cancellationToken);
            return await completion.Task;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public ValueTask NotifyAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        return WriteAsync(CreateMessage(null, method, parameters), cancellationToken);
    }

    public ValueTask StartNotificationPumpAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        var process = _process;
        _process = null;
        if (process is not null)
        {
            try { process.StandardInput.Close(); }
            catch { }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }
            }
            process.Dispose();
        }
        foreach (var pending in _pending.Values)
            pending.TrySetException(new McpTransportException("MCP stdio 连接已关闭。"));
        if (_readLoop is not null)
        {
            try { await _readLoop; }
            catch { }
        }
        if (_errorLoop is not null)
        {
            try { await _errorLoop; }
            catch { }
        }
        _writeGate.Dispose();
        _shutdown.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _process!.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                    break;
                if (line.Length > MaxMessageCharacters)
                    throw new McpTransportException("MCP stdio 消息超过大小限制。");
                using var document = JsonDocument.Parse(line);
                await HandleMessageAsync(document.RootElement, cancellationToken);
            }
            throw new McpTransportException("MCP stdio Server 已退出。", CreateExitException());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(exception);
        }
    }

    private async ValueTask HandleMessageAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (message.TryGetProperty("id", out var idElement)
            && idElement.ValueKind == JsonValueKind.Number
            && idElement.TryGetInt64(out var id)
            && _pending.TryRemove(id, out var completion))
        {
            if (message.TryGetProperty("result", out var result))
                completion.TrySetResult(result.Clone());
            else
                completion.TrySetException(new McpTransportException(ReadRpcError(message)));
            return;
        }
        if (!message.TryGetProperty("method", out var methodElement)
            || methodElement.ValueKind != JsonValueKind.String)
        {
            return;
        }
        var method = methodElement.GetString()!;
        var parameters = message.TryGetProperty("params", out var paramsElement)
            ? paramsElement.Clone()
            : EmptyObject();
        if (message.TryGetProperty("id", out idElement) && idElement.ValueKind != JsonValueKind.Null)
        {
            await WriteAsync(CreateErrorMessage(idElement, -32601, "Client method not supported."), cancellationToken);
            return;
        }
        await InvokeNotificationAsync(method, parameters, cancellationToken);
    }

    private async ValueTask WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _process!.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw new McpTransportException("写入 MCP stdio 失败。", exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task DrainStandardErrorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _process!.StandardError.ReadLineAsync(cancellationToken) is not null)
            {
                // 防止子进程 stderr 管道阻塞；不记录正文，避免服务端意外输出凭据。
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Exception? CreateExitException() =>
        _process is { HasExited: true } ? new InvalidOperationException($"退出码：{_process.ExitCode}") : null;

    private void EnsureStarted()
    {
        if (_process is null)
            throw new InvalidOperationException("MCP stdio transport 尚未启动。");
    }

    private async ValueTask InvokeNotificationAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var handlers = NotificationReceived;
        if (handlers is null)
            return;
        foreach (Func<string, JsonElement, CancellationToken, ValueTask> handler in handlers.GetInvocationList())
            await handler(method, parameters, cancellationToken);
    }

    internal static JsonObject CreateMessage(long? id, string method, JsonElement parameters)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = JsonNode.Parse(parameters.GetRawText()),
        };
        if (id is not null)
            message["id"] = id.Value;
        return message;
    }

    private static JsonObject CreateErrorMessage(JsonElement id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = JsonNode.Parse(id.GetRawText()),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    internal static string ReadRpcError(JsonElement message)
    {
        if (!message.TryGetProperty("error", out var error))
            return "MCP Server 返回无 result 的响应。";
        var code = error.TryGetProperty("code", out var codeElement) ? codeElement.GetRawText() : "unknown";
        var text = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : "unknown error";
        return $"MCP JSON-RPC 错误 {code}：{text}";
    }

    internal static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}

internal sealed class HttpMcpTransport : IMcpJsonRpcTransport
{
    private readonly McpServerProfile _profile;
    private readonly IAiCredentialStore _credentials;
    private readonly Lazy<Task<HttpClient>> _client;
    private readonly CancellationTokenSource _shutdown = new();
    private long _nextId;
    private string? _sessionId;
    private Task? _notificationPump;
    private int _disposed;

    public HttpMcpTransport(McpServerProfile profile, IAiCredentialStore credentials)
    {
        _profile = profile;
        _credentials = credentials;
        _client = new Lazy<Task<HttpClient>>(
            () => CreateClient(profile, credentials),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public event Func<string, JsonElement, CancellationToken, ValueTask>? NotificationReceived;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _ = await _client.Value.WaitAsync(cancellationToken);
    }

    public async ValueTask<JsonElement> RequestAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var id = Interlocked.Increment(ref _nextId);
        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            StdioMcpTransport.CreateMessage(id, method, parameters).ToJsonString(),
            cancellationToken);
        using var response = await SendAsync(request, cancellationToken);
        return await ReadResponseAsync(response, id, cancellationToken);
    }

    public async ValueTask NotifyAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var request = await CreateRequestAsync(
            HttpMethod.Post,
            StdioMcpTransport.CreateMessage(null, method, parameters).ToJsonString(),
            cancellationToken);
        using var response = await SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Accepted && !response.IsSuccessStatusCode)
            throw new McpTransportException($"MCP HTTP notification 返回 {(int)response.StatusCode}。");
    }

    public ValueTask StartNotificationPumpAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_notificationPump is null && _sessionId is not null)
            _notificationPump = NotificationPumpAsync(_shutdown.Token);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _shutdown.Cancel();
        if (_notificationPump is not null)
        {
            try { await _notificationPump; }
            catch { }
        }
        if (_client.IsValueCreated)
        {
            try { (await _client.Value).Dispose(); }
            catch { }
        }
        _shutdown.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_profile.Timeout);
        try
        {
            var client = await _client.Value.WaitAsync(timeout.Token);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
                _sessionId = values.FirstOrDefault();
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new McpTransportException($"MCP HTTP 返回 {(int)response.StatusCode}。");
            }
            return response;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpTransportException("MCP HTTP 请求超时。", exception);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);

    private async ValueTask<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string? json,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, _profile.Endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_sessionId is not null)
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        await ApplyAuthenticationAsync(request, cancellationToken);
        return request;
    }

    private async ValueTask ApplyAuthenticationAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_profile.Authentication.Kind == AiAuthenticationKind.None)
            return;
        var credential = await _credentials.GetAsync(
            _profile.Authentication.CredentialReference,
            cancellationToken);
        if (credential is null)
            throw new McpTransportException("MCP HTTP 认证凭据不可用。");
        request.Headers.TryAddWithoutValidation(
            _profile.Authentication.HeaderName,
            _profile.Authentication.HeaderPrefix + credential.Reveal());
    }

    private async ValueTask<JsonElement> ReadResponseAsync(
        HttpResponseMessage response,
        long expectedId,
        CancellationToken cancellationToken)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var message in ReadSseMessagesAsync(response, cancellationToken))
            {
                if (await TryHandleIncomingAsync(message, expectedId, cancellationToken) is { } result)
                    return result;
            }
            throw new McpTransportException("MCP HTTP SSE 未返回匹配的 JSON-RPC 响应。");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var handled = await TryHandleIncomingAsync(document.RootElement, expectedId, cancellationToken);
        return handled ?? throw new McpTransportException("MCP HTTP 返回了不匹配的 JSON-RPC 响应。");
    }

    private async Task NotificationPumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = await CreateRequestAsync(HttpMethod.Get, null, cancellationToken);
            var client = await _client.Value.WaitAsync(cancellationToken);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode
                || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            await foreach (var message in ReadSseMessagesAsync(response, cancellationToken))
                await TryHandleIncomingAsync(message, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // 通知流失败不使已完成的请求失败；下一次显式刷新仍可恢复工具列表。
        }
    }

    private async ValueTask<JsonElement?> TryHandleIncomingAsync(
        JsonElement message,
        long? expectedId,
        CancellationToken cancellationToken)
    {
        if (message.TryGetProperty("id", out var idElement)
            && idElement.ValueKind == JsonValueKind.Number
            && idElement.TryGetInt64(out var id)
            && expectedId == id)
        {
            if (message.TryGetProperty("result", out var result))
                return result.Clone();
            throw new McpTransportException(StdioMcpTransport.ReadRpcError(message));
        }
        if (message.TryGetProperty("method", out var methodElement)
            && methodElement.ValueKind == JsonValueKind.String)
        {
            var parameters = message.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : StdioMcpTransport.EmptyObject();
            await InvokeNotificationAsync(methodElement.GetString()!, parameters, cancellationToken);
        }
        return null;
    }

    private async IAsyncEnumerable<JsonElement> ReadSseMessagesAsync(
        HttpResponseMessage response,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    using var document = JsonDocument.Parse(data.ToString());
                    yield return document.RootElement.Clone();
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
        }
    }

    private async ValueTask InvokeNotificationAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        var handlers = NotificationReceived;
        if (handlers is null)
            return;
        foreach (Func<string, JsonElement, CancellationToken, ValueTask> handler in handlers.GetInvocationList())
            await handler(method, parameters, cancellationToken);
    }

    private static async Task<HttpClient> CreateClient(
        McpServerProfile profile,
        IAiCredentialStore credentials)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(profile.Timeout.TotalSeconds, 30)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        switch (profile.Proxy.Mode)
        {
            case AiProxyMode.Direct:
                handler.UseProxy = false;
                break;
            case AiProxyMode.Custom:
                handler.UseProxy = true;
                var proxy = new WebProxy(profile.Proxy.Address!, false, profile.Proxy.BypassList.ToArray());
                var username = await ReadCredentialAsync(credentials, profile.Proxy.UsernameCredentialReference);
                var password = await ReadCredentialAsync(credentials, profile.Proxy.PasswordCredentialReference);
                if (username is not null || password is not null)
                    proxy.Credentials = new NetworkCredential(username ?? string.Empty, password ?? string.Empty);
                handler.Proxy = proxy;
                break;
            default:
                handler.UseProxy = true;
                break;
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<string?> ReadCredentialAsync(
        IAiCredentialStore credentials,
        string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        return (await credentials.GetAsync(reference))?.Reveal();
    }
}
