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

public sealed class WorkerSupervisor(IWorkerTransportFactory transportFactory)
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
            var result = await _transport.ReceiveAsync<WorkerExecutionResultPayload>(cancellationToken);
            if (result.RequestId != requestId || result.ExecutionId != executionId)
                throw new WorkerProtocolException(new ScriptDiagnostic(
                    "WORKER_REQUEST_MISMATCH", "Worker 返回的 requestId 或 executionId 不匹配。",
                    ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Validation));
            return result;
        }
        finally
        {
            State = State == WorkerState.Busy ? WorkerState.Ready : State;
            _executionGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await StopTransportAsync(cancellationToken);
        WorkerId = null;
        State = WorkerState.Stopped;
    }

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

public sealed class WorkerProtocolException(ScriptDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    public ScriptDiagnostic Diagnostic { get; } = diagnostic;
}
