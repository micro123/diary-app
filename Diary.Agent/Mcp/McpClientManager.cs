using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Tools;

namespace Diary.Agent.Mcp;

public sealed record McpServerDiagnostic(
    string ServerId,
    bool Connected,
    int ToolCount,
    string? Error);

public sealed class McpClientManager(
    AiConnectionManager settings,
    IAiCredentialStore credentials,
    IAgentConfirmationService confirmations) : IAsyncDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, ConnectionEntry> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpServerDiagnostic> _diagnostics = new(StringComparer.Ordinal);
    private AgentToolRegistry? _registry;
    private int _disposed;

    public IReadOnlyList<McpServerDiagnostic> Diagnostics
    {
        get
        {
            lock (_diagnostics)
                return _diagnostics.Values.OrderBy(item => item.ServerId, StringComparer.Ordinal).ToArray();
        }
    }

    public async ValueTask RefreshToolsAsync(
        AgentToolRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _registry = registry;
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var profiles = settings.Settings.McpServers
                .Where(profile => profile.Enabled)
                .ToDictionary(profile => profile.Id, StringComparer.Ordinal);
            foreach (var removed in _connections.Keys.Where(id => !profiles.ContainsKey(id)).ToArray())
            {
                var entry = _connections[removed];
                _connections.Remove(removed);
                registry.ReplaceOwnerTools(OwnerId(removed), []);
                await entry.Connection.DisposeAsync();
            }
            foreach (var profile in profiles.Values)
                await RefreshServerAsync(profile, registry, cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async ValueTask<McpRemoteCallResult> CallToolAsync(
        string serverId,
        string remoteToolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_connections.TryGetValue(serverId, out var entry))
            throw new InvalidOperationException($"MCP Server {serverId} 未连接。");
        return await entry.Connection.CallToolAsync(remoteToolName, arguments, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _refreshGate.WaitAsync();
        try
        {
            _registry = null;
            foreach (var entry in _connections.Values)
                await entry.Connection.DisposeAsync();
            _connections.Clear();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async ValueTask RefreshServerAsync(
        McpServerProfile profile,
        AgentToolRegistry registry,
        CancellationToken cancellationToken)
    {
        var fingerprint = CreateFingerprint(profile);
        if (!_connections.TryGetValue(profile.Id, out var entry)
            || !string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            if (entry is not null)
                await entry.Connection.DisposeAsync();
            var connection = new McpClientConnection(profile, credentials);
            connection.ToolsChanged += OnToolsChangedAsync;
            entry = new ConnectionEntry(fingerprint, connection);
            _connections[profile.Id] = entry;
        }
        try
        {
            var remoteTools = await entry.Connection.ListToolsAsync(cancellationToken);
            var policies = profile.Tools
                .Where(policy => policy.Enabled)
                .ToDictionary(policy => policy.ToolName, StringComparer.Ordinal);
            var adapters = remoteTools
                .Where(tool => policies.ContainsKey(tool.Name))
                .Where(tool => !McpToolPolicyGuard.IsProhibitedDestructiveToolName(tool.Name))
                .Select(tool => new McpAgentTool(
                    this,
                    confirmations,
                    profile,
                    tool,
                    policies[tool.Name].Risk,
                    CreateModelName(profile.Id, tool.Name)))
                .Cast<IAgentTool>()
                .ToArray();
            registry.ReplaceOwnerTools(OwnerId(profile.Id), adapters);
            lock (_diagnostics)
                _diagnostics[profile.Id] = new McpServerDiagnostic(profile.Id, true, adapters.Length, null);
        }
        catch (Exception exception)
        {
            registry.ReplaceOwnerTools(OwnerId(profile.Id), []);
            lock (_diagnostics)
            {
                _diagnostics[profile.Id] = new McpServerDiagnostic(
                    profile.Id,
                    false,
                    0,
                    exception.Message);
            }
        }
    }

    private ValueTask OnToolsChangedAsync(McpClientConnection connection)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return ValueTask.CompletedTask;
        var registry = _registry;
        if (registry is null)
            return ValueTask.CompletedTask;
        _ = Task.Run(async () =>
        {
            try
            {
                var profile = settings.Settings.McpServers.FirstOrDefault(item => item.Id == connection.Profile.Id);
                if (profile is not null && profile.Enabled)
                    await RefreshToolsAsync(registry);
            }
            catch
            {
                // 通知触发的刷新失败保留诊断；下一次 run 会再次显式刷新。
            }
        });
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0,
        this);

    private static string OwnerId(string serverId) => $"diary.ai-agent.mcp.{serverId}";

    private static string CreateModelName(string serverId, string toolName)
    {
        var raw = $"mcp_{serverId}_{toolName}";
        var sanitized = new string(raw.Select(character =>
            char.IsAsciiLetterOrDigit(character) ? character : '_').ToArray());
        if (sanitized.Length <= 64)
            return sanitized;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..10].ToLowerInvariant();
        return sanitized[..53] + "_" + hash;
    }

    private static string CreateFingerprint(McpServerProfile profile) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile)));

    private sealed record ConnectionEntry(string Fingerprint, McpClientConnection Connection);

    private sealed class McpAgentTool(
        McpClientManager manager,
        IAgentConfirmationService confirmationService,
        McpServerProfile server,
        McpRemoteTool remoteTool,
        AgentToolRisk risk,
        string modelName) : IAgentTool
    {
        public AgentToolDescriptor Descriptor { get; } = new(
            $"mcp.{server.Id}.{remoteTool.Name}",
            modelName,
            remoteTool.Title ?? remoteTool.Name,
            remoteTool.Description ?? $"调用 MCP Server {server.DisplayName} 的工具 {remoteTool.Name}。",
            remoteTool.InputSchema,
            AgentToolOrigin.Mcp,
            risk,
            OwnerId(server.Id));

        public async ValueTask<AgentToolResult> InvokeAsync(
            JsonElement arguments,
            AgentToolInvocationContext context,
            CancellationToken cancellationToken = default)
        {
            if (risk != AgentToolRisk.ReadOnly)
            {
                var decision = await confirmationService.ConfirmExternalToolAsync(
                    new ExternalToolConfirmationRequest(
                        Guid.NewGuid(),
                        context.RunId,
                        context.InvocationId,
                        server.DisplayName,
                        remoteTool.Name,
                        arguments.Clone()),
                    cancellationToken);
                if (decision != AgentConfirmationDecision.Confirm)
                    return AgentToolResult.Failure("user_rejected", "用户拒绝了 MCP 写工具调用。");
            }
            try
            {
                var result = await manager.CallToolAsync(server.Id, remoteTool.Name, arguments, cancellationToken);
                var content = result.Result.GetRawText();
                return new AgentToolResult(
                    !result.IsError,
                    content,
                    result.IsError ? "mcp_tool_error" : null,
                    $"mcp://{server.Id}/{remoteTool.Name}",
                    IsExternalContent: true,
                    EffectSummary: risk == AgentToolRisk.ReadOnly
                        ? null
                        : $"已调用 MCP 工具 {server.Id}/{remoteTool.Name}。");
            }
            catch (Exception exception) when (exception is McpTransportException or InvalidOperationException)
            {
                return AgentToolResult.Failure("mcp_call_failed", exception.Message);
            }
        }
    }
}
