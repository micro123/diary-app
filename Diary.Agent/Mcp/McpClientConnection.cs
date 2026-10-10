using System.Text.Json;
using System.Text.Json.Nodes;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.Agent.Mcp;

public sealed record McpRemoteTool(
    string Name,
    string? Title,
    string? Description,
    JsonElement InputSchema);

public sealed record McpRemoteCallResult(
    JsonElement Result,
    bool IsError);

public sealed record McpConnectionRuntimeInfo(
    bool Connected,
    int? ProcessId,
    int? ExitCode,
    string? SessionId);

public sealed class McpClientConnection : IAsyncDisposable
{
    private const string ProtocolVersion = "2025-11-25";
    private readonly IMcpJsonRpcTransport _transport;
    private readonly ILogger<McpClientConnection> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private bool _connected;
    private int _disposed;

    public McpClientConnection(
        McpServerProfile profile,
        IAiCredentialStore credentials,
        ILoggerFactory? loggerFactory = null,
        ILogger<McpClientConnection>? logger = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        Profile = profile;
        _logger = logger ?? NullLogger<McpClientConnection>.Instance;
        _transport = profile.Transport switch
        {
            McpTransportKind.Stdio => new StdioMcpTransport(
                profile,
                loggerFactory.CreateLogger<StdioMcpTransport>()),
            McpTransportKind.StreamableHttp => new HttpMcpTransport(
                profile,
                credentials,
                loggerFactory.CreateLogger<HttpMcpTransport>()),
            _ => throw new ArgumentOutOfRangeException(nameof(profile.Transport)),
        };
        _transport.NotificationReceived += OnNotificationAsync;
    }

    public McpServerProfile Profile { get; }

    public McpConnectionRuntimeInfo RuntimeInfo
    {
        get
        {
            var transport = _transport.RuntimeInfo;
            return new McpConnectionRuntimeInfo(
                _connected,
                transport.ProcessId,
                transport.ExitCode,
                transport.SessionId);
        }
    }

    public event Func<McpClientConnection, ValueTask>? ToolsChanged;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_connected)
            return;
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_connected)
                return;
            _logger.LogInformation(
                "正在初始化 MCP Server。ServerId={ServerId}, Transport={Transport}",
                Profile.Id,
                Profile.Transport);
            await _transport.StartAsync(cancellationToken);
            using var initializeDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "DiaryApp AI Agent", version = "1.0" },
            }));
            var initialize = await _transport.RequestAsync(
                "initialize",
                initializeDocument.RootElement,
                cancellationToken);
            if (!initialize.TryGetProperty("protocolVersion", out _))
                throw new McpTransportException("MCP initialize 响应缺少 protocolVersion。");
            await _transport.NotifyAsync(
                "notifications/initialized",
                StdioMcpTransport.EmptyObject(),
                cancellationToken);
            await _transport.StartNotificationPumpAsync(cancellationToken);
            _connected = true;
            _logger.LogInformation(
                "MCP Server 初始化完成。ServerId={ServerId}, ProtocolVersion={ProtocolVersion}, ProcessId={ProcessId}, SessionId={SessionId}",
                Profile.Id,
                initialize.GetProperty("protocolVersion").GetString(),
                RuntimeInfo.ProcessId,
                RuntimeInfo.SessionId);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<McpRemoteTool>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken);
        var tools = new List<McpRemoteTool>();
        string? cursor = null;
        do
        {
            var parameters = cursor is null
                ? StdioMcpTransport.EmptyObject()
                : JsonSerializer.SerializeToElement(new { cursor });
            var result = await _transport.RequestAsync("tools/list", parameters, cancellationToken);
            if (!result.TryGetProperty("tools", out var toolsElement)
                || toolsElement.ValueKind != JsonValueKind.Array)
            {
                throw new McpTransportException("MCP tools/list 响应缺少 tools 数组。");
            }
            foreach (var tool in toolsElement.EnumerateArray())
            {
                if (tools.Count >= 500)
                    throw new McpTransportException("单个 MCP Server 的工具数量超过 500 个限制。");
                var name = tool.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var inputSchema = tool.TryGetProperty("inputSchema", out var schema)
                    ? schema.Clone()
                    : StdioMcpTransport.EmptyObject();
                tools.Add(new McpRemoteTool(
                    name,
                    tool.TryGetProperty("title", out var title) ? title.GetString() : null,
                    tool.TryGetProperty("description", out var description) ? description.GetString() : null,
                    inputSchema));
            }
            cursor = result.TryGetProperty("nextCursor", out var nextCursor)
                     && nextCursor.ValueKind == JsonValueKind.String
                ? nextCursor.GetString()
                : null;
        } while (!string.IsNullOrWhiteSpace(cursor));
        _logger.LogInformation(
            "MCP tools/list 完成。ServerId={ServerId}, ToolCount={ToolCount}, Tools={Tools}",
            Profile.Id,
            tools.Count,
            string.Join(',', tools.Take(50).Select(tool => tool.Name)));
        return tools;
    }

    public async ValueTask<McpRemoteCallResult> CallToolAsync(
        string name,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken);
        var parameters = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = JsonNode.Parse(arguments.GetRawText()),
        };
        using var document = JsonDocument.Parse(parameters.ToJsonString());
        var result = await _transport.RequestAsync("tools/call", document.RootElement, cancellationToken);
        var isError = result.TryGetProperty("isError", out var isErrorElement)
                      && isErrorElement.ValueKind == JsonValueKind.True;
        return new McpRemoteCallResult(result.Clone(), isError);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _connected = false;
        _transport.NotificationReceived -= OnNotificationAsync;
        await _transport.DisposeAsync();
        _connectGate.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);

    private async ValueTask OnNotificationAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(method, "notifications/tools/list_changed", StringComparison.Ordinal))
            return;
        var handlers = ToolsChanged;
        if (handlers is null)
            return;
        foreach (Func<McpClientConnection, ValueTask> handler in handlers.GetInvocationList())
            await handler(this);
    }
}
