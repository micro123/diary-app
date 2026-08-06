using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class WorkerHostCallTests
{
    [TestMethod]
    public async Task ExecuteAsync_DispatchesHostCallAndReturnsHostResult()
    {
        var transport = new HostCallTransport();
        var dispatcher = new RecordingDispatcher();
        var supervisor = new WorkerSupervisor(new HostCallFactory(transport), dispatcher);
        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], ["workItems.query"]));

        var result = await supervisor.ExecuteAsync("demo", "exec-1", new { });

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status);
        Assert.AreEqual("workItems.query", dispatcher.Method);
        Assert.IsTrue(transport.Sent.Any(message => message.Type == WorkerMessageType.HostResult));
    }

    private sealed class RecordingDispatcher : IWorkerHostCallDispatcher
    {
        public string? Method { get; private set; }

        public ValueTask<WorkerHostResultPayload> DispatchAsync(string executionId, WorkerHostCallPayload call, CancellationToken cancellationToken = default)
        {
            Method = call.Method;
            return ValueTask.FromResult(new WorkerHostResultPayload(true, JsonSerializer.SerializeToElement(new { items = Array.Empty<object>() })));
        }
    }

    private sealed class HostCallFactory(HostCallTransport transport) : IWorkerTransportFactory
    {
        public ValueTask<IWorkerTransport> CreateAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IWorkerTransport>(transport);
    }

    private sealed class HostCallTransport : IWorkerTransport
    {
        public List<WorkerMessage<object>> Sent { get; } = [];
        private int _receiveCount;

        public ValueTask SendAsync<TPayload>(WorkerMessage<TPayload> message, CancellationToken cancellationToken = default)
        {
            Sent.Add(new(message.Protocol, message.Version, message.Type, message.RequestId, message.ExecutionId, message.Payload!));
            return ValueTask.CompletedTask;
        }

        public ValueTask<WorkerMessage<TPayload>> ReceiveAsync<TPayload>(CancellationToken cancellationToken = default)
        {
            _receiveCount++;
            object message = _receiveCount switch
            {
                1 => new WorkerMessage<WorkerHelloPayload>(WorkerProtocol.Name, 1, WorkerMessageType.Hello, "hello", null,
                    new("csharp", "1", [ScriptApiVersion.V1], ["workItems.query"], 1)),
                2 => new WorkerMessage<JsonElement>(WorkerProtocol.Name, 1, WorkerMessageType.HostCall, "host-1", "exec-1",
                    JsonSerializer.SerializeToElement(new WorkerHostCallPayload("workItems.query", JsonSerializer.SerializeToElement(new { })))),
                _ => new WorkerMessage<JsonElement>(WorkerProtocol.Name, 1, WorkerMessageType.ExecuteResult, Sent.First(message => message.Type == WorkerMessageType.Execute).RequestId, "exec-1",
                    JsonSerializer.SerializeToElement(new WorkerExecutionResultPayload(ScriptExecutionStatus.Succeeded, []))),
            };
            return ValueTask.FromResult((WorkerMessage<TPayload>)message);
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
