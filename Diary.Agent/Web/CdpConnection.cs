using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diary.Agent.Web;

internal sealed record CdpEvent(string Method, JsonElement Parameters, string? SessionId);

internal sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _receiveTask;
    private long _nextId;

    public event Func<CdpEvent, ValueTask>? EventReceived;

    public async ValueTask ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(endpoint, cancellationToken);
        _receiveTask = ReceiveAsync(_shutdown.Token);
    }

    public async ValueTask<JsonElement> SendAsync(
        string method,
        JsonObject? parameters = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("无法注册 CDP 请求。");
        try
        {
            var message = new JsonObject
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new JsonObject(),
            };
            if (!string.IsNullOrWhiteSpace(sessionId))
                message["sessionId"] = sessionId;
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendGate.Release();
            }
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _socket.Abort();
        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        }
        FailPending(new ObjectDisposedException(nameof(CdpConnection)));
        _socket.Dispose();
        _shutdown.Dispose();
        _sendGate.Dispose();
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;
                Dispatch(message.ToArray());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailPending(exception);
            throw;
        }
    }

    private void Dispatch(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var idElement))
        {
            var id = idElement.GetInt64();
            if (!_pending.TryRemove(id, out var completion))
                return;
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : error.GetRawText();
                completion.TrySetException(new BrowserPageReadException(
                    "cdp_error",
                    $"浏览器 CDP 调用失败：{message}"));
            }
            else
            {
                var result = root.TryGetProperty("result", out var resultElement)
                    ? resultElement.Clone()
                    : JsonSerializer.SerializeToElement(new { });
                completion.TrySetResult(result);
            }
            return;
        }
        if (!root.TryGetProperty("method", out var methodElement))
            return;
        var handler = EventReceived;
        if (handler is null)
            return;
        var cdpEvent = new CdpEvent(
            methodElement.GetString() ?? string.Empty,
            root.TryGetProperty("params", out var parameters)
                ? parameters.Clone()
                : JsonSerializer.SerializeToElement(new { }),
            root.TryGetProperty("sessionId", out var sessionId) ? sessionId.GetString() : null);
        _ = Task.Run(async () =>
        {
            try
            {
                await handler(cdpEvent);
            }
            catch
            {
                // 单个事件处理失败由页面读取流程的超时或结果校验收敛。
            }
        });
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var completion))
                completion.TrySetException(exception);
        }
    }
}
