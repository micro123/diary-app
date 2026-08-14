using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class WorkerSupervisorTests
{
    [TestMethod]
    public async Task StartAsync_HandshakesAndExecuteIsSerialized()
    {
        var transport = new FakeTransport();
        var supervisor = new WorkerSupervisor(new FakeFactory(transport));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        var result = await supervisor.ExecuteAsync("demo", "exec-1", new { value = 1 });

        Assert.AreEqual(WorkerState.Ready, supervisor.State);
        Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status);
        Assert.AreEqual(WorkerMessageType.HelloAccepted, transport.Sent[0].Type);
        Assert.AreEqual(WorkerMessageType.Execute, transport.Sent[1].Type);
        await supervisor.StopAsync();
        Assert.AreEqual(WorkerState.Stopped, supervisor.State);
    }

    [TestMethod]
    public async Task StartAsync_RejectsMismatchedHandshake()
    {
        var transport = new FakeTransport(language: "python");
        var supervisor = new WorkerSupervisor(new FakeFactory(transport));

        await Assert.ThrowsExactlyAsync<WorkerProtocolException>(() =>
            supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], [])).AsTask());

        Assert.AreEqual(WorkerState.Failed, supervisor.State);
    }

    [TestMethod]
    public async Task CheckHealthAsync_RequiresMatchingPong()
    {
        var transport = new FakeTransport();
        var supervisor = new WorkerSupervisor(new FakeFactory(transport));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        Assert.IsTrue(await supervisor.CheckHealthAsync());
    }

    [TestMethod]
    public async Task StartAsync_HandshakeTimeoutFailsWorker()
    {
        var transport = new FakeTransport { DelayHello = true };
        var supervisor = new WorkerSupervisor(new FakeFactory(transport), handshakeTimeout: TimeSpan.FromMilliseconds(50));

        var exception = await Assert.ThrowsExactlyAsync<WorkerProtocolException>(() =>
            supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], [])).AsTask());

        Assert.AreEqual("WORKER_HANDSHAKE_TIMED_OUT", exception.Diagnostic.Code);
        Assert.AreEqual(WorkerState.Failed, supervisor.State);
        Assert.IsTrue(transport.StopCalled);
    }

    [TestMethod]
    public async Task Supervisor_SendsHeartbeatWhileReadyAndStaysReady()
    {
        var transport = new FakeTransport();
        var supervisor = new WorkerSupervisor(
            new FakeFactory(transport),
            heartbeatInterval: TimeSpan.FromMilliseconds(50),
            heartbeatTimeout: TimeSpan.FromMilliseconds(500),
            resourceCheckInterval: TimeSpan.FromMilliseconds(20));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        await Task.Delay(200);

        Assert.AreEqual(WorkerState.Ready, supervisor.State);
        Assert.IsTrue(transport.Sent.Count(message => message.Type == WorkerMessageType.Ping) >= 2);
        await supervisor.StopAsync();
    }

    [TestMethod]
    public async Task Supervisor_ReclaimsWorkerOnHeartbeatTimeout()
    {
        var transport = new FakeTransport { SuppressPong = true };
        var supervisor = new WorkerSupervisor(
            new FakeFactory(transport),
            heartbeatInterval: TimeSpan.FromMilliseconds(50),
            heartbeatTimeout: TimeSpan.FromMilliseconds(30),
            resourceCheckInterval: TimeSpan.FromMilliseconds(20));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        await WaitForStateAsync(supervisor, WorkerState.Failed);

        Assert.IsTrue(transport.StopCalled);
    }

    [TestMethod]
    public async Task Supervisor_SkipsHeartbeatWhileBusy()
    {
        var transport = new FakeTransport { DelayExecute = true };
        var supervisor = new WorkerSupervisor(
            new FakeFactory(transport),
            heartbeatInterval: TimeSpan.FromMilliseconds(20),
            heartbeatTimeout: TimeSpan.FromMilliseconds(100),
            resourceCheckInterval: TimeSpan.FromMilliseconds(10));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        var execution = supervisor.ExecuteAsync("demo", "exec-busy", new { }, TimeSpan.FromSeconds(2)).AsTask();
        await transport.ExecuteSent.Task;
        await Task.Delay(100);
        Assert.IsFalse(transport.Sent.Any(message => message.Type == WorkerMessageType.Ping));

        transport.CompleteExecution();
        var result = await execution;
        Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status);
    }

    [TestMethod]
    public async Task ExecuteAsync_HostCallTimeoutFailsWorker()
    {
        var transport = new FakeTransport { EmitHostCall = true };
        var supervisor = new WorkerSupervisor(
            new FakeFactory(transport),
            new BlockingDispatcher(),
            hostCallTimeout: TimeSpan.FromMilliseconds(50));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        var result = await supervisor.ExecuteAsync("demo", "exec-hostcall-timeout", new { });

        Assert.AreEqual(ScriptExecutionStatus.Failed, result.Payload.Status);
        Assert.AreEqual("WORKER_HOST_CALL_TIMED_OUT", result.Payload.Diagnostics.Single().Code);
        Assert.AreEqual(WorkerState.Failed, supervisor.State);
        Assert.IsTrue(transport.StopCalled);
    }

    [TestMethod]
    public async Task ExecuteAsync_DispatchesHostCallSuccessfully()
    {
        var transport = new FakeTransport { EmitHostCall = true };
        var dispatcher = new RecordingDispatcher();
        var supervisor = new WorkerSupervisor(new FakeFactory(transport), dispatcher);
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        var result = await supervisor.ExecuteAsync("demo", "exec-hostcall", new { });

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status);
        Assert.AreEqual(WorkerState.Ready, supervisor.State);
        Assert.AreEqual("workItems.query", dispatcher.LastCall!.Method);
        Assert.IsTrue(transport.Sent.Any(message => message.Type == WorkerMessageType.HostResult));
    }

    private sealed class BlockingDispatcher : IWorkerHostCallDispatcher
    {
        public async ValueTask<WorkerHostResultPayload> DispatchAsync(
            string executionId, WorkerHostCallPayload call, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(true);
        }
    }

    private sealed class RecordingDispatcher : IWorkerHostCallDispatcher
    {
        public WorkerHostCallPayload? LastCall { get; private set; }

        public ValueTask<WorkerHostResultPayload> DispatchAsync(
            string executionId, WorkerHostCallPayload call, CancellationToken cancellationToken = default)
        {
            LastCall = call;
            return ValueTask.FromResult(new WorkerHostResultPayload(true));
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_TimeoutReturnsStructuredResultAndFailsWorker()
    {
        var transport = new FakeTransport { DelayExecute = true };
        var supervisor = new WorkerSupervisor(new FakeFactory(transport));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        var result = await supervisor.ExecuteAsync("demo", "exec-timeout", new { }, TimeSpan.FromMilliseconds(10));

        Assert.AreEqual(ScriptExecutionStatus.TimedOut, result.Payload.Status);
        Assert.AreEqual(WorkerState.Failed, supervisor.State);
        Assert.IsTrue(transport.Sent.Any(message => message.Type == WorkerMessageType.Cancel));
    }

    [TestMethod]
    public async Task ExecuteAsync_CancelledWorkerResultIsReclaimedGracefully()
    {
        var transport = new FakeTransport { DelayExecute = true, CompleteOnCancel = true };
        var supervisor = new WorkerSupervisor(new FakeFactory(transport), cancellationGracePeriod: TimeSpan.FromMilliseconds(100));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));
        using var cancellation = new CancellationTokenSource();

        var execution = supervisor.ExecuteAsync("demo", "exec-cancelled", new { }, cancellationToken: cancellation.Token).AsTask();
        await transport.ExecuteSent.Task;
        cancellation.Cancel();

        var result = await execution;

        Assert.AreEqual(ScriptExecutionStatus.Cancelled, result.Payload.Status);
        Assert.AreEqual(WorkerState.Ready, supervisor.State);
        Assert.IsTrue(transport.Sent.Any(message => message.Type == WorkerMessageType.Cancel));
    }

    [TestMethod]
    public async Task ExecuteAsync_TerminatedWorkerReturnsStructuredFailureAndFailsWorker()
    {
        var transport = new FakeTransport { TerminateExecute = true };
        var supervisor = new WorkerSupervisor(new FakeFactory(transport));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        var result = await supervisor.ExecuteAsync("demo", "exec-terminated", new { });

        Assert.AreEqual(ScriptExecutionStatus.Failed, result.Payload.Status);
        Assert.AreEqual("WORKER_TERMINATED", result.Payload.Diagnostics.Single().Code);
        Assert.AreEqual(WorkerState.Failed, supervisor.State);
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsOversizedMessageBeforeSending()
    {
        var transport = new FakeTransport();
        var supervisor = new WorkerSupervisor(new FakeFactory(transport), maxExecuteMessageBytes: 100);
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        var result = await supervisor.ExecuteAsync("demo", "exec-large", new { value = new string('x', 200) });

        Assert.AreEqual(ScriptExecutionStatus.Failed, result.Payload.Status);
        Assert.AreEqual("WORKER_MESSAGE_TOO_LARGE", result.Payload.Diagnostics.Single().Code);
        Assert.AreEqual(WorkerState.Failed, supervisor.State);
        Assert.IsFalse(transport.Sent.Any(message => message.Type == WorkerMessageType.Execute));
    }

    [TestMethod]
    public async Task Supervisor_ReclaimsWorkerWhenWorkingSetExceedsLimit()
    {
        var transport = new FakeTransport { WorkingSetBytes = 100 };
        var supervisor = new WorkerSupervisor(
            new FakeFactory(transport),
            maxWorkingSetBytes: 50,
            resourceCheckInterval: TimeSpan.FromMilliseconds(10));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        await WaitForStateAsync(supervisor, WorkerState.Failed);

        Assert.AreEqual(WorkerState.Failed, supervisor.State);
        Assert.IsTrue(transport.StopCalled);
    }

    [TestMethod]
    public async Task Supervisor_ReclaimsWorkerWhenStderrExceedsLimit()
    {
        var transport = new FakeTransport { StderrLimitExceeded = true };
        var supervisor = new WorkerSupervisor(
            new FakeFactory(transport),
            resourceCheckInterval: TimeSpan.FromMilliseconds(10));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        await WaitForStateAsync(supervisor, WorkerState.Failed);

        Assert.AreEqual(WorkerState.Failed, supervisor.State);
        Assert.IsTrue(transport.StopCalled);
    }

    private static async Task WaitForStateAsync(WorkerSupervisor supervisor, WorkerState expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (supervisor.State != expected && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.AreEqual(expected, supervisor.State);
    }

    [TestMethod]
    public async Task Supervisor_ReclaimsIdleWorkerInBackground()
    {
        var transport = new FakeTransport();
        var supervisor = new WorkerSupervisor(new FakeFactory(transport), idleTimeout: TimeSpan.FromMilliseconds(20));
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

        await Task.Delay(100);

        Assert.AreEqual(WorkerState.Stopped, supervisor.State);
    }

    private sealed class FakeFactory(FakeTransport transport) : IWorkerTransportFactory
    {
        public ValueTask<IWorkerTransport> CreateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IWorkerTransport>(transport);
    }

    private sealed class FakeTransport(string language = "csharp") : IWorkerTransport, IWorkerTerminationNotification, IWorkerResourceUsage
    {
        public List<WorkerMessage<object>> Sent { get; } = [];
        public bool DelayExecute { get; init; }
        public bool TerminateExecute { get; init; }
        public bool CompleteOnCancel { get; init; }
        public bool DelayHello { get; init; }
        public bool SuppressPong { get; init; }
        public bool EmitHostCall { get; init; }
        public TaskCompletionSource<bool> ExecuteSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private EventHandler<WorkerTerminatedEventArgs>? _terminated;
        public event EventHandler<WorkerTerminatedEventArgs>? Terminated
        {
            add => _terminated += value;
            remove => _terminated -= value;
        }
        public int? ExitCode => null;
        public bool StderrLimitExceeded { get; init; }
        public long? WorkingSetBytes { get; init; }
        public bool StopCalled { get; private set; }
        private string? _executeRequestId;
        private string? _executionId;
        private int _receiveCount;
        private readonly Queue<object> _responses = new([
            new WorkerMessage<WorkerHelloPayload>(WorkerProtocol.Name, 1, WorkerMessageType.Hello, "hello", null,
                new WorkerHelloPayload(language, "1", [ScriptApiVersion.V1], [], 1))]);

        public void CompleteExecution() =>
            _responses.Enqueue(new WorkerMessage<WorkerExecutionResultPayload>(WorkerProtocol.Name, 1, WorkerMessageType.ExecuteResult,
                _executeRequestId, _executionId, new(ScriptExecutionStatus.Succeeded, [])));

        public ValueTask SendAsync<TPayload>(WorkerMessage<TPayload> message, CancellationToken cancellationToken = default)
        {
            Sent.Add(new WorkerMessage<object>(message.Protocol, message.Version, message.Type, message.RequestId, message.ExecutionId, message.Payload!));
            if (message.Type == WorkerMessageType.Execute)
            {
                _executeRequestId = message.RequestId;
                _executionId = message.ExecutionId;
                ExecuteSent.TrySetResult(true);
                if (!DelayExecute && !TerminateExecute && !EmitHostCall)
                    _responses.Enqueue(new WorkerMessage<WorkerExecutionResultPayload>(WorkerProtocol.Name, 1, WorkerMessageType.ExecuteResult,
                        message.RequestId, message.ExecutionId, new(ScriptExecutionStatus.Succeeded, [])));
                else if (EmitHostCall)
                    _responses.Enqueue(new WorkerMessage<WorkerHostCallPayload>(WorkerProtocol.Name, 1, WorkerMessageType.HostCall,
                        "host-call-1", message.ExecutionId, new("workItems.query", JsonSerializer.SerializeToElement(new { }))));
            }
            else if (message.Type == WorkerMessageType.Cancel && CompleteOnCancel)
            {
                _responses.Enqueue(new WorkerMessage<WorkerExecutionResultPayload>(WorkerProtocol.Name, 1, WorkerMessageType.ExecuteResult,
                    _executeRequestId, _executionId, new(ScriptExecutionStatus.Succeeded, [])));
            }
            else if (message.Type == WorkerMessageType.HostResult)
            {
                _responses.Enqueue(new WorkerMessage<WorkerExecutionResultPayload>(WorkerProtocol.Name, 1, WorkerMessageType.ExecuteResult,
                    _executeRequestId, _executionId, new(ScriptExecutionStatus.Succeeded, [])));
            }
            else if (message.Type == WorkerMessageType.Ping && !SuppressPong)
                _responses.Enqueue(new WorkerMessage<object>(WorkerProtocol.Name, 1, WorkerMessageType.Pong,
                    message.RequestId, null, new { }));
            return ValueTask.CompletedTask;
        }

        public async ValueTask<WorkerMessage<TPayload>> ReceiveAsync<TPayload>(CancellationToken cancellationToken = default)
        {
            if (DelayHello && _receiveCount++ == 0)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            while (_responses.Count == 0 && (DelayExecute || SuppressPong))
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
            if (TerminateExecute && _responses.Count == 0)
                throw new EndOfStreamException("worker exited");
            var json = JsonSerializer.Serialize(_responses.Dequeue(), WorkerProtocol.JsonOptions);
            return JsonSerializer.Deserialize<WorkerMessage<TPayload>>(json, WorkerProtocol.JsonOptions)!;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
