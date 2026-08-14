using System.Collections.Concurrent;
using System.Text.Json;
using Diary.Script.Runtime;

internal sealed class WorkerMessageWriter(Stream output)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>常规消息（HostCall、Pong、Error 等）的上限，由 HelloAccepted 协商设置。</summary>
    public int MaxMessageBytes { get; set; } = WorkerProtocol.DefaultMaxMessageBytes;

    public async ValueTask WriteAsync<TPayload>(WorkerMessage<TPayload> message, int? maxMessageBytes = null)
    {
        await _gate.WaitAsync();
        try
        {
            await WorkerMessageCodec.WriteAsync(output, message, maxMessageBytes ?? MaxMessageBytes);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed class WorkerHostCallRouter(Stream output)
{
    private readonly WorkerMessageWriter _writer = new(output);
    private readonly ConcurrentDictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);

    public int MaxMessageBytes
    {
        get => _writer.MaxMessageBytes;
        set => _writer.MaxMessageBytes = value;
    }

    public ValueTask WriteAsync<TPayload>(WorkerMessage<TPayload> message, int? maxMessageBytes = null) =>
        _writer.WriteAsync(message, maxMessageBytes);

    public async ValueTask<WorkerHostResultPayload> CallAsync(
        WorkerHostCallPayload call,
        string executionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestId = Guid.NewGuid().ToString("N");
        var pending = new PendingCall(executionId);
        if (!_pending.TryAdd(requestId, pending))
            throw new InvalidOperationException("无法注册 Worker 宿主调用。");

        try
        {
            await _writer.WriteAsync(new WorkerMessage<WorkerHostCallPayload>(
                WorkerProtocol.Name, WorkerProtocol.Version, WorkerMessageType.HostCall,
                requestId, executionId, call));
            return await pending.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    public bool TryComplete(WorkerMessage<JsonElement> message)
    {
        if (message.Type != WorkerMessageType.HostResult || message.RequestId is null)
            return false;
        var pending = _pending.GetValueOrDefault(message.RequestId);
        if (pending is null)
            return false;
        var result = message.Payload.Deserialize<WorkerHostResultPayload>(WorkerProtocol.JsonOptions);
        if (result is null)
            pending.Completion.TrySetException(new InvalidDataException("Worker 宿主返回结果无效。"));
        else
            pending.Completion.TrySetResult(result);
        return true;
    }

    public void CancelExecution(string executionId)
    {
        foreach (var pending in _pending.Values.Where(item => item.ExecutionId == executionId))
            pending.Completion.TrySetCanceled();
    }

    private sealed class PendingCall(string executionId)
    {
        public string ExecutionId { get; } = executionId;
        public TaskCompletionSource<WorkerHostResultPayload> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
