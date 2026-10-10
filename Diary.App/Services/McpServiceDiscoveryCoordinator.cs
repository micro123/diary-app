using Diary.Survey;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Services;

public sealed class McpServiceDiscoveryCoordinator(
    AppSurveyor surveyor,
    AppRespondent respondent,
    ExtendedSurveyServiceRegistry registry,
    ExtendedSurveyServiceDirectory directory,
    ILogger<McpServiceDiscoveryCoordinator> logger) : IAsyncDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromSeconds(90);
    private readonly Dictionary<string, DateTimeOffset> _activeRequests = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly string _localHostname = SysInfo.GetHostname();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public bool TryHandleRequest(string content)
    {
        if (!ExtendedSurveyProtocol.TryDeserializeRequest(content, out var request)
            || request is not { Kind: ExtendedSurveyProtocol.McpServicesKind })
        {
            return false;
        }

        respondent.Send(ExtendedSurveyProtocol.SerializeMcpServicesSuccess(
            request.RequestId,
            SysInfo.GetHostname(),
            SysInfo.GetUsername(),
            _instanceId,
            registry.Snapshot()));
        return true;
    }

    public bool TryHandleResponse(string content)
    {
        if (!ExtendedSurveyProtocol.TryDeserializeMcpServicesResponse(content, out var response, out var data)
            || response is null
            || data is null)
        {
            return false;
        }

        lock (_sync)
        {
            PruneRequestsLocked(DateTimeOffset.UtcNow);
            if (!_activeRequests.ContainsKey(response.RequestId))
            {
                logger.LogDebug(
                    "忽略不属于当前自动发现轮次的 MCP 服务响应。RequestId={RequestId}",
                    response.RequestId);
                return true;
            }
        }

        if (IsLocalHost(data.Hostname, _localHostname))
        {
            logger.LogDebug(
                "忽略本机 MCP 服务发现响应。InstanceId={InstanceId}, Hostname={Hostname}",
                data.InstanceId,
                data.Hostname);
            return true;
        }

        directory.Apply(data, DateTimeOffset.UtcNow);
        logger.LogDebug(
            "收到 MCP 服务发现响应。InstanceId={InstanceId}, Hostname={Hostname}, ServiceCount={ServiceCount}",
            data.InstanceId,
            data.Hostname,
            data.Services.Count);
        return true;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_loopTask is { IsCompleted: false })
                return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? loopTask;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _cts;
            loopTask = _loopTask;
            _cts = null;
            _loopTask = null;
            cts?.Cancel();
            _activeRequests.Clear();
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts?.IsCancellationRequested == true)
            {
            }
        }
        cts?.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    internal static bool IsLocalHost(string? responseHostname, string? localHostname) =>
        !string.IsNullOrWhiteSpace(responseHostname)
        && !string.IsNullOrWhiteSpace(localHostname)
        && string.Equals(
            responseHostname.Trim(),
            localHostname.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(DefaultInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            await SurveyAsync(cancellationToken).ConfigureAwait(false);
            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                break;
        }
    }

    private async Task SurveyAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            PruneRequestsLocked(now);
            _activeRequests[requestId] = now + RequestLifetime;
        }

        directory.Prune(now);
        logger.LogDebug("发起 MCP 服务自动发现。RequestId={RequestId}", requestId);
        await surveyor.SurveyAsync(ExtendedSurveyProtocol.SerializeMcpServicesRequest(requestId))
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private void PruneRequestsLocked(DateTimeOffset nowUtc)
    {
        foreach (var requestId in _activeRequests
                     .Where(item => item.Value <= nowUtc)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _activeRequests.Remove(requestId);
        }
    }
}
