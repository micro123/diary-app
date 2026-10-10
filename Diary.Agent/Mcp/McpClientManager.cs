using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Credentials;
using Diary.Agent.Tools;

namespace Diary.Agent.Mcp;

public enum McpServerRuntimeState
{
    Stopped,
    Connecting,
    Connected,
    Stopping,
    Faulted,
}

public sealed record McpServerDiagnostic(
    string ServerId,
    string DisplayName,
    McpTransportKind Transport,
    bool Enabled,
    McpServerRuntimeState State,
    int DiscoveredToolCount,
    int ExposedToolCount,
    int? ProcessId,
    int? ExitCode,
    string? SessionId,
    string? Error,
    DateTimeOffset UpdatedAtUtc);

public sealed record McpServerTestResult(
    bool Succeeded,
    IReadOnlyList<McpRemoteTool> Tools,
    string Message)
{
    public int DiscoveredToolCount => Tools.Count;
}

public sealed class McpClientManager(
    AiConnectionManager settings,
    IAiCredentialStore credentials,
    IAgentConfirmationService confirmations) : IAsyncDisposable
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpServerDiagnostic> _diagnostics = new(StringComparer.Ordinal);
    private AgentToolRegistry? _registry;
    private int _disposed;

    public IReadOnlyList<McpServerDiagnostic> Diagnostics
    {
        get
        {
            lock (_diagnostics)
            {
                return settings.Settings.McpServers
                    .Select(profile => _diagnostics.TryGetValue(profile.Id, out var diagnostic)
                        ? diagnostic with
                        {
                            DisplayName = profile.DisplayName,
                            Transport = profile.Transport,
                            Enabled = profile.Enabled,
                        }
                        : CreateDiagnostic(profile, McpServerRuntimeState.Stopped))
                    .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)
                    .ThenBy(item => item.ServerId, StringComparer.Ordinal)
                    .ToArray();
            }
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
                await StopServerCoreAsync(removed, registry, cancellationToken);
            foreach (var profile in profiles.Values)
                await ConnectServerCoreAsync(profile, registry, restart: false, cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async ValueTask<McpServerDiagnostic> StartServerAsync(
        string serverId,
        AgentToolRegistry registry,
        CancellationToken cancellationToken = default)
    {
        var profile = GetSavedProfile(serverId, requireEnabled: true);
        return await RunLifecycleAsync(
            registry,
            cancellationToken,
            () => ConnectServerCoreAsync(profile, registry, restart: false, cancellationToken));
    }

    public async ValueTask<McpServerDiagnostic> StopServerAsync(
        string serverId,
        AgentToolRegistry registry,
        CancellationToken cancellationToken = default) =>
        await RunLifecycleAsync(
            registry,
            cancellationToken,
            () => StopServerCoreAsync(serverId, registry, cancellationToken));

    public async ValueTask<McpServerDiagnostic> RestartServerAsync(
        string serverId,
        AgentToolRegistry registry,
        CancellationToken cancellationToken = default)
    {
        var profile = GetSavedProfile(serverId, requireEnabled: true);
        return await RunLifecycleAsync(
            registry,
            cancellationToken,
            () => ConnectServerCoreAsync(profile, registry, restart: true, cancellationToken));
    }

    public async ValueTask<McpServerDiagnostic> RefreshServerToolsAsync(
        string serverId,
        AgentToolRegistry registry,
        CancellationToken cancellationToken = default)
    {
        var profile = GetSavedProfile(serverId, requireEnabled: true);
        return await RunLifecycleAsync(
            registry,
            cancellationToken,
            () => ConnectServerCoreAsync(profile, registry, restart: false, cancellationToken));
    }

    public async ValueTask<McpServerTestResult> TestServerAsync(
        string serverId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var profile = GetSavedProfile(serverId, requireEnabled: false);
        await using var connection = new McpClientConnection(profile, credentials);
        try
        {
            var tools = await connection.ListToolsAsync(cancellationToken);
            var names = string.Join("、", tools.Take(12).Select(tool => tool.Name));
            var suffix = tools.Count > 12 ? $" 等 {tools.Count} 个" : string.Empty;
            var detail = tools.Count == 0 ? string.Empty : $"：{names}{suffix}";
            return new McpServerTestResult(true, tools, $"连接成功，发现 {tools.Count} 个工具{detail}。");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new McpServerTestResult(false, [], exception.Message);
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
        await entry.OperationGate.WaitAsync(cancellationToken);
        try
        {
            if (!_connections.TryGetValue(serverId, out var current) || !ReferenceEquals(current, entry))
                throw new InvalidOperationException($"MCP Server {serverId} 正在停止或重启。");
            try
            {
                return await entry.Connection.CallToolAsync(remoteToolName, arguments, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var profile = entry.Connection.Profile;
                UpdateDiagnostic(CreateDiagnostic(
                    profile,
                    McpServerRuntimeState.Faulted,
                    runtime: entry.Connection.RuntimeInfo,
                    error: exception.Message));
                throw;
            }
        }
        finally
        {
            entry.OperationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _refreshGate.WaitAsync();
        try
        {
            _registry = null;
            foreach (var serverId in _connections.Keys.ToArray())
                await StopServerCoreAsync(serverId, null, CancellationToken.None);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async ValueTask<McpServerDiagnostic> RunLifecycleAsync(
        AgentToolRegistry registry,
        CancellationToken cancellationToken,
        Func<ValueTask<McpServerDiagnostic>> operation)
    {
        ThrowIfDisposed();
        _registry = registry;
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await operation();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async ValueTask<McpServerDiagnostic> ConnectServerCoreAsync(
        McpServerProfile profile,
        AgentToolRegistry registry,
        bool restart,
        CancellationToken cancellationToken)
    {
        var fingerprint = CreateFingerprint(profile);
        if (restart)
            await StopServerCoreAsync(profile.Id, registry, cancellationToken, keepStoppingState: true);
        if (!_connections.TryGetValue(profile.Id, out var entry)
            || !string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            if (entry is not null)
                await RemoveEntryAsync(profile.Id, entry, registry, cancellationToken);
            var connection = new McpClientConnection(profile, credentials);
            connection.ToolsChanged += OnToolsChangedAsync;
            entry = new ConnectionEntry(fingerprint, connection);
            _connections[profile.Id] = entry;
        }
        UpdateDiagnostic(CreateDiagnostic(profile, McpServerRuntimeState.Connecting));
        await entry.OperationGate.WaitAsync(cancellationToken);
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
            var diagnostic = CreateDiagnostic(
                profile,
                McpServerRuntimeState.Connected,
                remoteTools.Count,
                adapters.Length,
                entry.Connection.RuntimeInfo);
            UpdateDiagnostic(diagnostic);
            return diagnostic;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            registry.ReplaceOwnerTools(OwnerId(profile.Id), []);
            _connections.TryRemove(new KeyValuePair<string, ConnectionEntry>(profile.Id, entry));
            var runtime = entry.Connection.RuntimeInfo;
            await entry.Connection.DisposeAsync();
            var diagnostic = CreateDiagnostic(
                profile,
                McpServerRuntimeState.Faulted,
                runtime: runtime,
                error: exception.Message);
            UpdateDiagnostic(diagnostic);
            return diagnostic;
        }
        finally
        {
            entry.OperationGate.Release();
        }
    }

    private async ValueTask<McpServerDiagnostic> StopServerCoreAsync(
        string serverId,
        AgentToolRegistry? registry,
        CancellationToken cancellationToken,
        bool keepStoppingState = false)
    {
        var profile = settings.Settings.McpServers.FirstOrDefault(item => item.Id == serverId);
        if (profile is null)
        {
            registry?.ReplaceOwnerTools(OwnerId(serverId), []);
            lock (_diagnostics)
                _diagnostics.Remove(serverId);
            return new McpServerDiagnostic(
                serverId,
                serverId,
                McpTransportKind.Stdio,
                false,
                McpServerRuntimeState.Stopped,
                0,
                0,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow);
        }
        UpdateDiagnostic(CreateDiagnostic(profile, McpServerRuntimeState.Stopping));
        if (_connections.TryRemove(serverId, out var entry))
            await DisposeEntryAsync(entry, cancellationToken);
        registry?.ReplaceOwnerTools(OwnerId(serverId), []);
        var diagnostic = CreateDiagnostic(
            profile,
            keepStoppingState ? McpServerRuntimeState.Stopping : McpServerRuntimeState.Stopped);
        UpdateDiagnostic(diagnostic);
        return diagnostic;
    }

    private async ValueTask RemoveEntryAsync(
        string serverId,
        ConnectionEntry entry,
        AgentToolRegistry registry,
        CancellationToken cancellationToken)
    {
        _connections.TryRemove(new KeyValuePair<string, ConnectionEntry>(serverId, entry));
        registry.ReplaceOwnerTools(OwnerId(serverId), []);
        await DisposeEntryAsync(entry, cancellationToken);
    }

    private static async ValueTask DisposeEntryAsync(
        ConnectionEntry entry,
        CancellationToken cancellationToken)
    {
        await entry.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await entry.Connection.DisposeAsync();
        }
        finally
        {
            entry.OperationGate.Release();
        }
    }

    private McpServerProfile GetSavedProfile(string serverId, bool requireEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        var profile = settings.Settings.McpServers.FirstOrDefault(item => item.Id == serverId)
            ?? throw new InvalidOperationException($"找不到已保存的 MCP Server：{serverId}。");
        if (requireEnabled && !profile.Enabled)
            throw new InvalidOperationException($"MCP Server {profile.DisplayName} 已禁用，请先修改配置并保存。");
        return profile;
    }

    private void UpdateDiagnostic(McpServerDiagnostic diagnostic)
    {
        lock (_diagnostics)
            _diagnostics[diagnostic.ServerId] = diagnostic;
    }

    private static McpServerDiagnostic CreateDiagnostic(
        McpServerProfile profile,
        McpServerRuntimeState state,
        int discoveredToolCount = 0,
        int exposedToolCount = 0,
        McpConnectionRuntimeInfo? runtime = null,
        string? error = null) => new(
        profile.Id,
        profile.DisplayName,
        profile.Transport,
        profile.Enabled,
        state,
        discoveredToolCount,
        exposedToolCount,
        runtime?.ProcessId,
        runtime?.ExitCode,
        runtime?.SessionId,
        error,
        DateTimeOffset.UtcNow);

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

    private sealed class ConnectionEntry(string fingerprint, McpClientConnection connection)
    {
        public string Fingerprint { get; } = fingerprint;

        public McpClientConnection Connection { get; } = connection;

        public SemaphoreSlim OperationGate { get; } = new(1, 1);
    }

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
