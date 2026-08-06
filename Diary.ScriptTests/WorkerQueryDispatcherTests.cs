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
    public async Task DispatchAsync_WithoutReadCapabilityReturnsPermissionDenied()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(_ => new FakeQueryApi());
        var result = await dispatcher.DispatchAsync("exec", ScriptCapability.None, new(
            "workItems.query", JsonSerializer.SerializeToElement(new ScriptWorkItemQuery())));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("PermissionDenied", result.Error!.Code);
    }

    private sealed class FakeQueryApi : IWorkItemQueryScriptApi
    {
        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(ScriptWorkItemQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptWorkItemQueryResult.Success(ImmutableArray<ScriptWorkItem>.Empty, query));
    }
}
