using System.Collections.Immutable;
using System.Text.Json;
using Diary.ScriptBase;
using Diary.ScriptHost;
using Diary.Script.Runtime;

namespace Diary.ScriptTests;

[TestClass]
public sealed class WorkerQueryDispatcherTests
{
    [TestMethod]
    public async Task DispatchAsync_RejectsUnknownMethod()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(_ => new FakeQueryApi());
        var result = await dispatcher.DispatchAsync("exec", ScriptCapability.ReadDiary, new("unknown", JsonSerializer.SerializeToElement(new { })));
        Assert.IsFalse(result.Success);
        Assert.AreEqual("InvalidInput", result.Error!.Code);
    }

    [TestMethod]
    public async Task DispatchAsync_ReturnsQueryResult()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(_ => new FakeQueryApi());
        var call = new WorkerHostCallPayload(
            "workItems.query", JsonSerializer.SerializeToElement(new ScriptWorkItemQuery { Limit = 10 }));
        var result = await dispatcher.DispatchAsync("exec", ScriptCapability.ReadDiary, call);
        Assert.IsTrue(result.Success);
        var queryResult = result.Result!.Value.Deserialize<ScriptWorkItemQueryResult>(WorkerProtocol.JsonOptions);
        Assert.IsTrue(queryResult!.Succeeded);
        Assert.AreEqual(10, queryResult.NormalizedQuery!.Limit);
    }

    [TestMethod]
    public async Task DispatchAsync_UsesApiWithoutPermissionGate()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(_ => new FakeQueryApi());
        var result = await dispatcher.DispatchAsync("exec", ScriptCapability.None, new(
            "workItems.query", JsonSerializer.SerializeToElement(new ScriptWorkItemQuery())));

        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    public async Task DispatchAsync_ReturnsTrackerInstanceForCSharpWorker()
    {
        var tracker = new FakeTrackerApi();
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            _ => new FakeQueryApi(),
            _ => tracker);

        var result = await dispatcher.DispatchAsync("exec", ScriptCapability.None, new(
            "trackerInstances.get",
            JsonSerializer.SerializeToElement(new { pluginId = "tracker.memory", instanceId = "company" })));

        Assert.IsTrue(result.Success);
        var instance = result.Result!.Value.Deserialize<ScriptTrackerInstance>(WorkerProtocol.JsonOptions);
        Assert.AreEqual("company", instance!.InstanceId);
    }

    private sealed class FakeQueryApi : IWorkItemQueryScriptApi
    {
        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(ScriptWorkItemQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptWorkItemQueryResult.Success(ImmutableArray<ScriptWorkItem>.Empty, query));
    }

    private sealed class FakeTrackerApi : ITrackerInstanceScriptApi
    {
        public TrackerScriptResult Get(string pluginId, string instanceId) =>
            TrackerScriptResult.Success(new(pluginId, instanceId, "Company", "memory", true));
    }
}
