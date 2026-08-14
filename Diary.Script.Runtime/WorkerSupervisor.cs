using System.Text.Json;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public enum WorkerState
{
    Stopped,
    Handshaking,
    Ready,
    Busy,
    Failed,
}

public interface IWorkerTransport : IAsyncDisposable
{
    ValueTask SendAsync<TPayload>(WorkerMessage<TPayload> message, CancellationToken cancellationToken = default);
    ValueTask<WorkerMessage<TPayload>> ReceiveAsync<TPayload>(
        CancellationToken cancellationToken = default,
        int maxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IWorkerTerminationNotification
{
    event EventHandler<WorkerTerminatedEventArgs>? Terminated;
    int? ExitCode { get; }
    bool StderrLimitExceeded { get; }
}

public sealed class WorkerTerminatedEventArgs(int? exitCode) : EventArgs
{
    public int? ExitCode { get; } = exitCode;
}

public interface IWorkerBoundedTransport
{
    int MaxMessageBytes { get; }
}

public interface IWorkerResourceUsage
{
    long? WorkingSetBytes { get; }
}

public interface IWorkerTransportFactory
{
    ValueTask<IWorkerTransport> CreateAsync(CancellationToken cancellationToken = default);
}

public interface IWorkerHostCallDispatcher
{
    ValueTask<WorkerHostResultPayload> DispatchAsync(
        string executionId,
        WorkerHostCallPayload call,
        CancellationToken cancellationToken = default);
}

public sealed class WorkerSupervisor(
    IWorkerTransportFactory transportFactory,
    IWorkerHostCallDispatcher? hostCallDispatcher = null,
    int maxHostCallsPerExecution = 100,
    int maxExecuteMessageBytes = WorkerProtocol.DefaultMaxMessageBytes,
    TimeSpan? idleTimeout = null,
    TimeSpan? restartBaseDelay = null,
    int maxRequestsPerWorker = 1000,
    int maxResultMessageBytes = WorkerProtocol.DefaultMaxResultMessageBytes,
    long? maxWorkingSetBytes = null,
    TimeSpan? cancellationGracePeriod = null,
    TimeSpan? resourceCheckInterval = null,
    TimeSpan? heartbeatInterval = null,
    TimeSpan? heartbeatTimeout = null,
    TimeSpan? handshakeTimeout = null,
    TimeSpan? hostCallTimeout = null)
{
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private IWorkerTransport? _transport;
    private WorkerHandshakeOptions? _handshakeOptions;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastPong = DateTimeOffset.UtcNow;
    private int _restartAttempts;
    private int _requestCount;
    private CancellationTokenSource? _lifetimeCancellation;
    private Task? _idleMonitor;

    public WorkerState State { get; private set; } = WorkerState.Stopped;
    public string? WorkerId { get; private set; }

    public int MaxHostCallsPerExecution { get; } = maxHostCallsPerExecution > 0
        ? maxHostCallsPerExecution
        : throw new ArgumentOutOfRangeException(nameof(maxHostCallsPerExecution));

    public int MaxExecuteMessageBytes { get; } = maxExecuteMessageBytes > 0
        ? maxExecuteMessageBytes
        : throw new ArgumentOutOfRangeException(nameof(maxExecuteMessageBytes));

    public TimeSpan IdleTimeout { get; } = idleTimeout ?? TimeSpan.FromMinutes(10);
    public TimeSpan RestartBaseDelay { get; } = restartBaseDelay ?? TimeSpan.FromMilliseconds(250);
    public int MaxRequestsPerWorker { get; } = maxRequestsPerWorker > 0
        ? maxRequestsPerWorker
        : throw new ArgumentOutOfRangeException(nameof(maxRequestsPerWorker));
    public int MaxResultMessageBytes { get; } = maxResultMessageBytes > 0
        ? maxResultMessageBytes
        : throw new ArgumentOutOfRangeException(nameof(maxResultMessageBytes));
    public long? MaxWorkingSetBytes { get; } = maxWorkingSetBytes is null or > 0
        ? maxWorkingSetBytes
        : throw new ArgumentOutOfRangeException(nameof(maxWorkingSetBytes));
    public TimeSpan ResourceCheckInterval { get; } = resourceCheckInterval is null || resourceCheckInterval.Value > TimeSpan.Zero
        ? resourceCheckInterval ?? TimeSpan.FromSeconds(1)
        : throw new ArgumentOutOfRangeException(nameof(resourceCheckInterval));
    public TimeSpan CancellationGracePeriod { get; } = cancellationGracePeriod is null || cancellationGracePeriod.Value >= TimeSpan.Zero
        ? cancellationGracePeriod ?? TimeSpan.FromMilliseconds(500)
        : throw new ArgumentOutOfRangeException(nameof(cancellationGracePeriod));

    public TimeSpan? HeartbeatInterval { get; } = heartbeatInterval is null || heartbeatInterval.Value > TimeSpan.Zero
        ? heartbeatInterval
        : throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));

    public TimeSpan HeartbeatTimeout { get; } = heartbeatTimeout is null || heartbeatTimeout.Value > TimeSpan.Zero
        ? heartbeatTimeout ?? TimeSpan.FromSeconds(WorkerProtocol.DefaultHeartbeatTimeoutSeconds)
        : throw new ArgumentOutOfRangeException(nameof(heartbeatTimeout));

    public TimeSpan HandshakeTimeout { get; } = handshakeTimeout is null || handshakeTimeout.Value > TimeSpan.Zero
        ? handshakeTimeout ?? TimeSpan.FromSeconds(WorkerProtocol.DefaultHandshakeTimeoutSeconds)
        : throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));

    public TimeSpan HostCallTimeout { get; } = hostCallTimeout is null || hostCallTimeout.Value > TimeSpan.Zero
        ? hostCallTimeout ?? TimeSpan.FromSeconds(WorkerProtocol.DefaultHostCallTimeoutSeconds)
        : throw new ArgumentOutOfRangeException(nameof(hostCallTimeout));

    public async ValueTask StartAsync(WorkerHandshakeOptions options, CancellationToken cancellationToken = default)
    {
        if (State is WorkerState.Ready or WorkerState.Busy)
        {
            if (State == WorkerState.Ready && DateTimeOffset.UtcNow - _lastActivity >= IdleTimeout)
                await StopAsync(cancellationToken);
            else
                return;
        }
        if (_restartAttempts > 0)
            await Task.Delay(GetRestartDelay(), cancellationToken);
        if (_transport is not null)
            await StopTransportAsync(cancellationToken);
        _handshakeOptions = options;
        _lastActivity = DateTimeOffset.UtcNow;
        State = WorkerState.Handshaking;
        using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCancellation.CancelAfter(HandshakeTimeout);
        try
        {
            _transport = await transportFactory.CreateAsync(handshakeCancellation.Token);
            if (_transport is IWorkerTerminationNotification notification)
                notification.Terminated += OnTransportTerminated;
            var hello = await _transport.ReceiveAsync<WorkerHelloPayload>(handshakeCancellation.Token);
            var handshake = WorkerHandshake.Negotiate(hello, options);
            if (!handshake.Accepted)
                throw new WorkerProtocolException(handshake.Diagnostic!);
            await _transport.SendAsync(new WorkerMessage<WorkerHelloAcceptedPayload>(
                WorkerProtocol.Name,
                WorkerProtocol.Version,
                WorkerMessageType.HelloAccepted,
                hello.RequestId,
                null,
                handshake.AcceptedPayload!), handshakeCancellation.Token);
            WorkerId = Guid.NewGuid().ToString("N");
            State = WorkerState.Ready;
            _restartAttempts = 0;
            _requestCount = 0;
            _lastPong = DateTimeOffset.UtcNow;
            StartIdleMonitor();
        }
        catch (OperationCanceledException) when (handshakeCancellation.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            State = WorkerState.Failed;
            _restartAttempts++;
            await StopTransportAsync(CancellationToken.None);
            throw new WorkerProtocolException(new ScriptDiagnostic(
                "WORKER_HANDSHAKE_TIMED_OUT", $"Worker 握手超时（{HandshakeTimeout}）。",
                ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime));
        }
        catch
        {
            State = WorkerState.Failed;
            _restartAttempts++;
            await StopTransportAsync(cancellationToken);
            throw;
        }
    }

    public async ValueTask<WorkerMessage<WorkerExecutionResultPayload>> ExecuteAsync(
        string scriptId,
        string executionId,
        object payload,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (State != WorkerState.Ready)
            throw new InvalidOperationException("Worker 尚未就绪。");
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            State = WorkerState.Busy;
            _lastActivity = DateTimeOffset.UtcNow;
            var requestId = Guid.NewGuid().ToString("N");
            var executeMessage = new WorkerMessage<object>(
                WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Execute,
                requestId, executionId, new { scriptId, payload });
            var messageBytes = JsonSerializer.SerializeToUtf8Bytes(executeMessage, WorkerProtocol.JsonOptions);
            // 发送上限同时受执行消息限制与握手协商的消息上限约束，保证宿主不会发送 Worker 读不下的消息。
            var sendLimit = Math.Min(MaxExecuteMessageBytes, _handshakeOptions?.MaxMessageBytes ?? MaxExecuteMessageBytes);
            if (messageBytes.Length + 1 > sendLimit)
            {
                State = WorkerState.Failed;
                return Result(requestId, executionId, ScriptExecutionStatus.Failed,
                    new ScriptDiagnostic("WORKER_MESSAGE_TOO_LARGE", "Worker 执行消息超过大小限制。",
                        ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Validation));
            }
            await _transport!.SendAsync(executeMessage, cancellationToken);
            _requestCount++;
            using var timeoutCancellation = timeout is null ? null : new CancellationTokenSource(timeout.Value);
            using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCancellation?.Token ?? CancellationToken.None);
            var cancellationSignal = WaitForCancellationAsync(cancellationToken);
            WorkerMessage<WorkerExecutionResultPayload> result;
            var hostCallCount = 0;
            try
            {
                while (true)
                {
                    // 执行期间的接收按结果上限（16MB）读取，使 WORKER_RESULT_TOO_LARGE 可达；
                    // HostCall 在下方按协议默认上限（4MB）单独检查。
                    var receiveTask = ReceiveMessageAsync(receiveCancellation.Token, MaxResultMessageBytes).AsTask();
                    if (cancellationToken.CanBeCanceled
                        && await Task.WhenAny(receiveTask, cancellationSignal) == cancellationSignal)
                    {
                        await TrySendCancelAsync(executionId, "Cancelled", null, CancellationToken.None);
                        var gracefulResult = await TryReceiveCancelledResultAsync(requestId, executionId, receiveTask);
                        if (gracefulResult is { } completed)
                            return MarkCancelledResult(completed);

                        State = WorkerState.Failed;
                        await StopTransportAsync(CancellationToken.None);
                        return Result(requestId, executionId, ScriptExecutionStatus.Cancelled);
                    }
                    var message = await receiveTask;
                    if (message.Type == WorkerMessageType.HostCall)
                    {
                        hostCallCount++;
                        if (hostCallCount > MaxHostCallsPerExecution)
                        {
                            State = WorkerState.Failed;
                            return Result(requestId, executionId, ScriptExecutionStatus.Failed,
                                new ScriptDiagnostic("WORKER_HOST_CALL_LIMIT", "Worker 宿主调用次数超过限制。",
                                    ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Security));
                        }
                        if (WorkerProtocol.GetMessageSize(message) > WorkerProtocol.DefaultMaxMessageBytes)
                        {
                            State = WorkerState.Failed;
                            return Result(requestId, executionId, ScriptExecutionStatus.Failed,
                                new ScriptDiagnostic("WORKER_HOST_CALL_TOO_LARGE", "Worker 宿主调用消息超过大小限制。",
                                    ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Security));
                        }
                        var call = message.Payload.Deserialize<WorkerHostCallPayload>(WorkerProtocol.JsonOptions)
                            ?? throw new WorkerProtocolException(new ScriptDiagnostic(
                                "WORKER_HOST_CALL_INVALID", "Worker 宿主调用载荷无效。", ScriptDiagnosticSeverity.Error,
                                ScriptDiagnosticCategory.Validation));
                        using var hostCallTimeoutCancellation = new CancellationTokenSource(HostCallTimeout);
                        using var hostCallCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            receiveCancellation.Token, cancellationToken, hostCallTimeoutCancellation.Token);
                        WorkerHostResultPayload hostResult;
                        try
                        {
                            hostResult = hostCallDispatcher is null
                                ? new WorkerHostResultPayload(false, Error: new("PermissionDenied", "当前 Worker 未配置宿主 API。"))
                                : await hostCallDispatcher.DispatchAsync(executionId, call, hostCallCancellation.Token);
                        }
                        catch (OperationCanceledException) when (hostCallTimeoutCancellation.IsCancellationRequested)
                        {
                            State = WorkerState.Failed;
                            await StopTransportAsync(CancellationToken.None);
                            return Result(requestId, executionId, ScriptExecutionStatus.Failed,
                                new ScriptDiagnostic("WORKER_HOST_CALL_TIMED_OUT",
                                    $"Worker 宿主调用 {call.Method} 超时（{HostCallTimeout}）。",
                                    ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime));
                        }
                        await _transport.SendAsync(new WorkerMessage<WorkerHostResultPayload>(
                            WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.HostResult,
                            message.RequestId, executionId, hostResult), receiveCancellation.Token);
                        continue;
                    }

                    if (message.Type != WorkerMessageType.ExecuteResult)
                        throw new WorkerProtocolException(new ScriptDiagnostic(
                            "WORKER_MESSAGE_UNEXPECTED", "Worker 在执行期间返回了意外消息。", ScriptDiagnosticSeverity.Error,
                            ScriptDiagnosticCategory.Validation));
                    result = new WorkerMessage<WorkerExecutionResultPayload>(
                        message.Protocol, message.Version, message.Type, message.RequestId, message.ExecutionId,
                        message.Payload.Deserialize<WorkerExecutionResultPayload>(WorkerProtocol.JsonOptions)
                             ?? throw new WorkerProtocolException(new ScriptDiagnostic(
                                "WORKER_RESULT_INVALID", "Worker 执行结果载荷无效。", ScriptDiagnosticSeverity.Error,
                                 ScriptDiagnosticCategory.Validation)));
                    if (WorkerProtocol.GetMessageSize(result) > MaxResultMessageBytes)
                    {
                        State = WorkerState.Failed;
                        return Result(requestId, executionId, ScriptExecutionStatus.Failed,
                            new ScriptDiagnostic("WORKER_RESULT_TOO_LARGE", "Worker 执行结果超过大小限制。",
                                ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime));
                    }
                    break;
                }
            }
            catch (OperationCanceledException) when (timeoutCancellation?.IsCancellationRequested == true)
            {
                await TrySendCancelAsync(executionId, "Timeout", DateTimeOffset.UtcNow, cancellationToken);
                State = WorkerState.Failed;
                await StopTransportAsync(CancellationToken.None);
                return Result(requestId, executionId, ScriptExecutionStatus.TimedOut,
                    new ScriptDiagnostic("SCRIPT_EXECUTION_TIMED_OUT", "Worker 执行超时。",
                        ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TrySendCancelAsync(executionId, "Cancelled", null, CancellationToken.None);
                var gracefulResult = await TryReceiveCancelledResultAsync(requestId, executionId);
                if (gracefulResult is { } completed)
                    return MarkCancelledResult(completed);

                State = WorkerState.Failed;
                await StopTransportAsync(CancellationToken.None);
                return Result(requestId, executionId, ScriptExecutionStatus.Cancelled);
            }
            catch (EndOfStreamException)
            {
                State = WorkerState.Failed;
                return Result(requestId, executionId, ScriptExecutionStatus.Failed,
                    TerminationDiagnostic("Worker 进程意外退出。"));
            }
            catch (IOException)
            {
                State = WorkerState.Failed;
                return Result(requestId, executionId, ScriptExecutionStatus.Failed,
                    TerminationDiagnostic("Worker 通道意外断开。"));
            }
            catch (WorkerMessageTooLargeException exception)
            {
                State = WorkerState.Failed;
                return Result(requestId, executionId, ScriptExecutionStatus.Failed,
                    new ScriptDiagnostic("WORKER_MESSAGE_TOO_LARGE", exception.Message,
                        ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime));
            }
            catch (WorkerProtocolDataException exception)
            {
                State = WorkerState.Failed;
                return Result(requestId, executionId, ScriptExecutionStatus.Failed,
                    new ScriptDiagnostic("WORKER_INVALID_MESSAGE", exception.Message,
                        ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime));
            }
            if (result.RequestId != requestId || result.ExecutionId != executionId)
                throw new WorkerProtocolException(new ScriptDiagnostic(
                    "WORKER_REQUEST_MISMATCH", "Worker 返回的 requestId 或 executionId 不匹配。",
                    ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Validation));
            if (cancellationToken.IsCancellationRequested)
                return MarkCancelledResult(result);
            if (_requestCount >= MaxRequestsPerWorker)
                State = WorkerState.Failed;
            return result;
        }
        finally
        {
            if (State == WorkerState.Busy)
                State = WorkerState.Ready;
            _lastActivity = DateTimeOffset.UtcNow;
            _executionGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation?.Dispose();
        _lifetimeCancellation = null;
        _idleMonitor = null;
        await StopTransportAsync(cancellationToken);
        WorkerId = null;
        State = WorkerState.Stopped;
    }

    public async ValueTask<bool> CheckHealthAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (State is not (WorkerState.Ready or WorkerState.Busy) || _transport is null)
            return false;
        var requestId = Guid.NewGuid().ToString("N");
        using var healthCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        healthCancellation.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            await _transport.SendAsync(new WorkerMessage<object>(
                WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Ping, requestId, null, new { }), healthCancellation.Token);
            var response = await _transport.ReceiveAsync<object>(healthCancellation.Token);
            return response.Type == WorkerMessageType.Pong && response.RequestId == requestId;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async ValueTask RestartAsync(WorkerHandshakeOptions options, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(options, cancellationToken);
    }

    private TimeSpan GetMonitorInterval() => IdleTimeout == Timeout.InfiniteTimeSpan
        ? ResourceCheckInterval
        : IdleTimeout < ResourceCheckInterval ? IdleTimeout : ResourceCheckInterval;

    private TimeSpan GetRestartDelay() =>
        TimeSpan.FromMilliseconds(Math.Min(RestartBaseDelay.TotalMilliseconds * Math.Pow(2, _restartAttempts - 1), 30_000));

    private void OnTransportTerminated(object? sender, WorkerTerminatedEventArgs args)
    {
        if (ReferenceEquals(sender, _transport) && State is WorkerState.Ready or WorkerState.Busy)
        {
            State = WorkerState.Failed;
            _restartAttempts++;
        }
    }

    private ScriptDiagnostic TerminationDiagnostic(string message) =>
        _transport is IWorkerTerminationNotification notification && notification.ExitCode is { } exitCode
            ? new ScriptDiagnostic("WORKER_TERMINATED", $"{message}（退出码：{exitCode}）。",
                ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime)
            : new ScriptDiagnostic("WORKER_TERMINATED", message,
                ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime);

    private void StartIdleMonitor()
    {
        if (IdleTimeout == Timeout.InfiniteTimeSpan)
            return;
        _lifetimeCancellation?.Cancel();
        _lifetimeCancellation?.Dispose();
        _lifetimeCancellation = new CancellationTokenSource();
        _idleMonitor = MonitorIdleAsync(_lifetimeCancellation.Token);
    }

    private async Task MonitorIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(GetMonitorInterval(), cancellationToken);
                if (HeartbeatInterval is { } heartbeat
                    && DateTimeOffset.UtcNow - _lastPong >= heartbeat)
                {
                    if (!await TryHeartbeatAsync(cancellationToken))
                        return;
                }
                if (State == WorkerState.Ready && IdleTimeout != Timeout.InfiniteTimeSpan
                    && DateTimeOffset.UtcNow - _lastActivity >= IdleTimeout)
                {
                    await StopAsync(CancellationToken.None);
                    return;
                }
                if (_transport is IWorkerTerminationNotification { StderrLimitExceeded: true })
                {
                    State = WorkerState.Failed;
                    await StopTransportAsync(CancellationToken.None);
                    return;
                }
                if (_transport is IWorkerResourceUsage usage
                    && MaxWorkingSetBytes is { } limit
                    && usage.WorkingSetBytes is { } workingSet
                    && workingSet > limit)
                {
                    State = WorkerState.Failed;
                    await StopTransportAsync(CancellationToken.None);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask<bool> TryHeartbeatAsync(CancellationToken cancellationToken)
    {
        // 仅 Ready 且能抢到执行门时 ping：门保证不与 ExecuteAsync 的接收循环并发，Pong 不会被截走。
        if (!await _executionGate.WaitAsync(0, cancellationToken))
            return true;
        try
        {
            if (State != WorkerState.Ready || _transport is null)
                return true;
            var requestId = Guid.NewGuid().ToString("N");
            await _transport.SendAsync(new WorkerMessage<object>(
                WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Ping, requestId, null, new { }), cancellationToken);
            using var pongCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pongCancellation.CancelAfter(HeartbeatTimeout);
            var response = await _transport.ReceiveAsync<object>(pongCancellation.Token);
            if (response.Type != WorkerMessageType.Pong || response.RequestId != requestId)
            {
                State = WorkerState.Failed;
                _restartAttempts++;
                await StopTransportAsync(CancellationToken.None);
                return false;
            }
            _lastPong = DateTimeOffset.UtcNow;
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            State = WorkerState.Failed;
            _restartAttempts++;
            await StopTransportAsync(CancellationToken.None);
            return false;
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ObjectDisposedException or WorkerProtocolDataException)
        {
            State = WorkerState.Failed;
            _restartAttempts++;
            await StopTransportAsync(CancellationToken.None);
            return false;
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private ValueTask<WorkerMessage<JsonElement>> ReceiveMessageAsync(
        CancellationToken cancellationToken,
        int maxMessageBytes = WorkerProtocol.DefaultMaxMessageBytes)
    {
        if (_transport is IWorkerTerminationNotification notification && notification.StderrLimitExceeded)
        {
            State = WorkerState.Failed;
            throw new IOException("Worker stderr 输出超过限制。");
        }
        return _transport!.ReceiveAsync<JsonElement>(cancellationToken, maxMessageBytes);
    }

    private async ValueTask<WorkerMessage<WorkerExecutionResultPayload>?> TryReceiveCancelledResultAsync(
        string requestId,
        string executionId,
        Task<WorkerMessage<JsonElement>>? pendingReceive = null)
    {
        if (_transport is null || CancellationGracePeriod <= TimeSpan.Zero)
            return null;

        using var graceCancellation = new CancellationTokenSource(CancellationGracePeriod);
        try
        {
            var receiveTask = pendingReceive ?? ReceiveMessageAsync(graceCancellation.Token, MaxResultMessageBytes).AsTask();
            var graceTask = Task.Delay(Timeout.InfiniteTimeSpan, graceCancellation.Token);
            while (true)
            {
                if (await Task.WhenAny(receiveTask, graceTask) != receiveTask)
                    return null;
                var message = await receiveTask;
                if (message.Type == WorkerMessageType.HostCall)
                {
                    await _transport.SendAsync(new WorkerMessage<WorkerHostResultPayload>(
                        WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.HostResult,
                        message.RequestId, executionId,
                        new(false, Error: new("Cancelled", "脚本执行已取消。"))),
                        graceCancellation.Token);
                    receiveTask = ReceiveMessageAsync(graceCancellation.Token, MaxResultMessageBytes).AsTask();
                    continue;
                }

                if (message.Type != WorkerMessageType.ExecuteResult)
                    return null;
                var payload = message.Payload.Deserialize<WorkerExecutionResultPayload>(WorkerProtocol.JsonOptions);
                if (payload is null || message.RequestId != requestId || message.ExecutionId != executionId)
                    return null;
                return new WorkerMessage<WorkerExecutionResultPayload>(
                    message.Protocol, message.Version, message.Type, message.RequestId, message.ExecutionId, payload);
            }
        }
        catch (OperationCanceledException) when (graceCancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ObjectDisposedException or WorkerProtocolDataException)
        {
            return null;
        }
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private WorkerMessage<WorkerExecutionResultPayload> MarkCancelledResult(
        WorkerMessage<WorkerExecutionResultPayload> result)
    {
        State = _requestCount >= MaxRequestsPerWorker ? WorkerState.Failed : WorkerState.Ready;
        return result with
        {
            Payload = result.Payload with { Status = ScriptExecutionStatus.Cancelled },
        };
    }

    private async ValueTask TrySendCancelAsync(
        string executionId,
        string reason,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_transport is not null)
                await _transport.SendAsync(new WorkerMessage<WorkerCancelPayload>(
                    WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Cancel,
                    Guid.NewGuid().ToString("N"), executionId, new(reason, deadline)), cancellationToken);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static WorkerMessage<WorkerExecutionResultPayload> Result(
        string requestId,
        string executionId,
        ScriptExecutionStatus status,
        params ScriptDiagnostic[] diagnostics) =>
        new(WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.ExecuteResult,
            requestId, executionId, new(status, diagnostics));

    private async ValueTask StopTransportAsync(CancellationToken cancellationToken)
    {
        if (_transport is null)
            return;
        var transport = _transport;
        _transport = null;
        if (transport is IWorkerTerminationNotification notification)
            notification.Terminated -= OnTransportTerminated;
        await transport.StopAsync(cancellationToken);
        await transport.DisposeAsync();
    }
}

public sealed record WorkerExecutionResultPayload(
    ScriptExecutionStatus Status,
    IReadOnlyCollection<ScriptDiagnostic> Diagnostics,
    object? Value = null,
    long DurationMilliseconds = 0,
    ScriptEffectSummary? Effects = null);

public sealed record WorkerCancelPayload(string Reason, DateTimeOffset? Deadline = null);

public sealed record WorkerErrorPayload(string Code, string Message);

public sealed record WorkerHostCallPayload(string Method, JsonElement Params);

public sealed record WorkerHostResultPayload(bool Success, JsonElement? Result = null, WorkerErrorPayload? Error = null);

public sealed class WorkerProtocolException(ScriptDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    public ScriptDiagnostic Diagnostic { get; } = diagnostic;
}
