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

    private sealed class FakeFactory(FakeTransport transport) : IWorkerTransportFactory
    {
        public ValueTask<IWorkerTransport> CreateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IWorkerTransport>(transport);
    }

    private sealed class FakeTransport(string language = "csharp") : IWorkerTransport
    {
        public List<WorkerMessage<object>> Sent { get; } = [];
        private readonly Queue<object> _responses = new([
            new WorkerMessage<WorkerHelloPayload>(WorkerProtocol.Name, 1, WorkerMessageType.Hello, "hello", null,
                new WorkerHelloPayload(language, "1", [ScriptApiVersion.V1], [], 1))]);

        public ValueTask SendAsync<TPayload>(WorkerMessage<TPayload> message, CancellationToken cancellationToken = default)
        {
            Sent.Add(new WorkerMessage<object>(message.Protocol, message.Version, message.Type, message.RequestId, message.ExecutionId, message.Payload!));
            if (message.Type == WorkerMessageType.Execute)
                _responses.Enqueue(new WorkerMessage<WorkerExecutionResultPayload>(WorkerProtocol.Name, 1, WorkerMessageType.ExecuteResult,
                    message.RequestId, message.ExecutionId, new(ScriptExecutionStatus.Succeeded, [])));
            return ValueTask.CompletedTask;
        }

        public ValueTask<WorkerMessage<TPayload>> ReceiveAsync<TPayload>(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult((WorkerMessage<TPayload>)_responses.Dequeue());

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
