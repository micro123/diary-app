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
    ValueTask<WorkerMessage<TPayload>> ReceiveAsync<TPayload>(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IWorkerTransportFactory
{
    ValueTask<IWorkerTransport> CreateAsync(CancellationToken cancellationToken = default);
}

public interface IWorkerHostCallDispatcher
{
    ValueTask<WorkerHostResultPayload> DispatchAsync(
        string executionId,
        ScriptCapability grantedCapabilities,
        WorkerHostCallPayload call,
        CancellationToken cancellationToken = default);
}

public sealed class WorkerSupervisor(
    IWorkerTransportFactory transportFactory,
    IWorkerHostCallDispatcher? hostCallDispatcher = null)
{
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private IWorkerTransport? _transport;

    public WorkerState State { get; private set; } = WorkerState.Stopped;
    public string? WorkerId { get; private set; }

    public async ValueTask StartAsync(WorkerHandshakeOptions options, CancellationToken cancellationToken = default)
    {
        if (State is WorkerState.Ready or WorkerState.Busy)
            return;
        await StopAsync(cancellationToken);
        State = WorkerState.Handshaking;
        try
        {
            _transport = await transportFactory.CreateAsync(cancellationToken);
            var hello = await _transport.ReceiveAsync<WorkerHelloPayload>(cancellationToken);
            var handshake = WorkerHandshake.Negotiate(hello, options);
            if (!handshake.Accepted)
                throw new WorkerProtocolException(handshake.Diagnostic!);
            await _transport.SendAsync(new WorkerMessage<WorkerHelloAcceptedPayload>(
                WorkerProtocol.Name,
                WorkerProtocol.Version,
                WorkerMessageType.HelloAccepted,
                hello.RequestId,
                null,
                handshake.AcceptedPayload!), cancellationToken);
            WorkerId = Guid.NewGuid().ToString("N");
            State = WorkerState.Ready;
        }
        catch
        {
            State = WorkerState.Failed;
            await StopTransportAsync(cancellationToken);
            throw;
        }
    }

    public async ValueTask<WorkerMessage<WorkerExecutionResultPayload>> ExecuteAsync(
        string scriptId,
        string executionId,
        object payload,
        TimeSpan? timeout = null,
        ScriptCapability grantedCapabilities = ScriptCapability.None,
        CancellationToken cancellationToken = default)
    {
        if (State != WorkerState.Ready)
            throw new InvalidOperationException("Worker 尚未就绪。");
        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            State = WorkerState.Busy;
            var requestId = Guid.NewGuid().ToString("N");
            await _transport!.SendAsync(new WorkerMessage<object>(
                WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Execute,
                requestId, executionId, new { scriptId, payload }), cancellationToken);
            using var timeoutCancellation = timeout is null ? null : new CancellationTokenSource(timeout.Value);
            using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCancellation?.Token ?? CancellationToken.None);
            WorkerMessage<WorkerExecutionResultPayload> result;
            try
            {
                while (true)
                {
                    var message = await _transport.ReceiveAsync<JsonElement>(receiveCancellation.Token);
                    if (message.Type == WorkerMessageType.HostCall)
                    {
                        var call = message.Payload.Deserialize<WorkerHostCallPayload>(WorkerProtocol.JsonOptions)
                            ?? throw new WorkerProtocolException(new ScriptDiagnostic(
                                "WORKER_HOST_CALL_INVALID", "Worker 宿主调用载荷无效。", ScriptDiagnosticSeverity.Error,
                                ScriptDiagnosticCategory.Validation));
                        var hostResult = hostCallDispatcher is null
                            ? new WorkerHostResultPayload(false, Error: new("PermissionDenied", "当前 Worker 未配置宿主 API。"))
                            : await hostCallDispatcher.DispatchAsync(executionId, grantedCapabilities, call, receiveCancellation.Token);
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
                    break;
                }
            }
            catch (OperationCanceledException) when (timeoutCancellation?.IsCancellationRequested == true)
            {
                await SendCancelAsync(executionId, "Timeout", DateTimeOffset.UtcNow, cancellationToken);
                State = WorkerState.Failed;
                return new WorkerMessage<WorkerExecutionResultPayload>(
                    WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.ExecuteResult,
                    requestId, executionId,
                    new(ScriptExecutionStatus.TimedOut, [new ScriptDiagnostic(
                        "SCRIPT_EXECUTION_TIMED_OUT", "Worker 执行超时。", ScriptDiagnosticSeverity.Error,
                        ScriptDiagnosticCategory.Runtime)]));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await SendCancelAsync(executionId, "Cancelled", null, CancellationToken.None);
                return new WorkerMessage<WorkerExecutionResultPayload>(
                    WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.ExecuteResult,
                    requestId, executionId,
                    new(ScriptExecutionStatus.Cancelled, []));
            }
            if (result.RequestId != requestId || result.ExecutionId != executionId)
                throw new WorkerProtocolException(new ScriptDiagnostic(
                    "WORKER_REQUEST_MISMATCH", "Worker 返回的 requestId 或 executionId 不匹配。",
                    ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Validation));
            return result;
        }
        finally
        {
            if (State == WorkerState.Busy)
                State = WorkerState.Ready;
            _executionGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await StopTransportAsync(cancellationToken);
        WorkerId = null;
        State = WorkerState.Stopped;
    }

    public async ValueTask<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (State is not (WorkerState.Ready or WorkerState.Busy) || _transport is null)
            return false;
        var requestId = Guid.NewGuid().ToString("N");
        await _transport.SendAsync(new WorkerMessage<object>(
            WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Ping, requestId, null, new { }), cancellationToken);
        var response = await _transport.ReceiveAsync<object>(cancellationToken);
        return response.Type == WorkerMessageType.Pong && response.RequestId == requestId;
    }

    public async ValueTask RestartAsync(WorkerHandshakeOptions options, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(options, cancellationToken);
    }

    private ValueTask SendCancelAsync(
        string executionId,
        string reason,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken) =>
        _transport!.SendAsync(new WorkerMessage<WorkerCancelPayload>(
            WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.Cancel,
            Guid.NewGuid().ToString("N"), executionId, new(reason, deadline)), cancellationToken);

    private async ValueTask StopTransportAsync(CancellationToken cancellationToken)
    {
        if (_transport is null)
            return;
        var transport = _transport;
        _transport = null;
        await transport.StopAsync(cancellationToken);
        await transport.DisposeAsync();
    }
}

public sealed record WorkerExecutionResultPayload(
    ScriptExecutionStatus Status,
    IReadOnlyCollection<ScriptDiagnostic> Diagnostics,
    object? Value = null,
    long DurationMilliseconds = 0);

public sealed record WorkerCancelPayload(string Reason, DateTimeOffset? Deadline = null);

public sealed record WorkerErrorPayload(string Code, string Message);

public sealed record WorkerHostCallPayload(string Method, JsonElement Params);

public sealed record WorkerHostResultPayload(bool Success, JsonElement? Result = null, WorkerErrorPayload? Error = null);

public sealed class WorkerProtocolException(ScriptDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    public ScriptDiagnostic Diagnostic { get; } = diagnostic;
}
