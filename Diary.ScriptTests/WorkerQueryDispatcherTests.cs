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
        var dispatcher = new WorkItemQueryWorkerDispatcher(() => new FakeQueryApi());
        var result = await dispatcher.DispatchAsync("exec", new("unknown", JsonSerializer.SerializeToElement(new { })));
        Assert.IsFalse(result.Success);
        Assert.AreEqual("InvalidInput", result.Error!.Code);
    }

    [TestMethod]
    public async Task DispatchAsync_ReturnsQueryResult()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(() => new FakeQueryApi());
        var call = new WorkerHostCallPayload(
            "workItems.query", JsonSerializer.SerializeToElement(new ScriptWorkItemQuery { Limit = 10 }));
        var result = await dispatcher.DispatchAsync("exec", call);
        Assert.IsTrue(result.Success);
        var queryResult = result.Result!.Value.Deserialize<ScriptWorkItemQueryResult>(WorkerProtocol.JsonOptions);
        Assert.IsTrue(queryResult!.Succeeded);
        Assert.AreEqual(10, queryResult.NormalizedQuery!.Limit);
    }

    [TestMethod]
    public async Task DispatchAsync_UsesApiWithoutPermissionGate()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(() => new FakeQueryApi());
        var result = await dispatcher.DispatchAsync("exec", new(
            "workItems.query", JsonSerializer.SerializeToElement(new ScriptWorkItemQuery())));

        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    public async Task DispatchAsync_ReturnsTrackerInstanceForCSharpWorker()
    {
        var tracker = new FakeTrackerApi();
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            () => tracker);

        var result = await dispatcher.DispatchAsync("exec", new(
            "trackerInstances.get",
            JsonSerializer.SerializeToElement(new { pluginId = "tracker.memory", instanceId = "company" })));

        Assert.IsTrue(result.Success);
        var instance = result.Result!.Value.Deserialize<ScriptTrackerInstance>(WorkerProtocol.JsonOptions);
        Assert.AreEqual("company", instance!.InstanceId);
    }

    [TestMethod]
    public async Task DispatchAsync_SupportsClipboardGetAndSet()
    {
        var clipboard = new FakeClipboardApi();
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(), clipboardApiFactory: () => clipboard);

        var set = await dispatcher.DispatchAsync("exec", new(
            "clipboard.set", JsonSerializer.SerializeToElement(new { text = "copied" })));
        var get = await dispatcher.DispatchAsync("exec", new(
            "clipboard.get", JsonSerializer.SerializeToElement(new { })));

        Assert.IsTrue(set.Success);
        Assert.IsTrue(get.Success);
        Assert.AreEqual("copied", get.Result!.Value.GetString());
    }

    [TestMethod]
    public async Task DispatchAsync_SupportsUserInteraction()
    {
        var interaction = new FakeInteractionApi();
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(), interactionApiFactory: () => interaction);
        var notify = await dispatcher.DispatchAsync("exec", new(
            "ui.notify", JsonSerializer.SerializeToElement(new { title = "Title", body = "Body" })));
        var confirm = await dispatcher.DispatchAsync("exec", new(
            "ui.confirm", JsonSerializer.SerializeToElement(new { title = "Confirm", body = "Continue?" })));

        Assert.IsTrue(notify.Success);
        Assert.IsTrue(confirm.Success);
        Assert.IsTrue(confirm.Result!.Value.GetBoolean());
        Assert.AreEqual("Confirm", interaction.Title);
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

    private sealed class FakeClipboardApi : IClipboardScriptApi
    {
        private string? _text;
        public ValueTask<string?> GetTextAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_text);
        public ValueTask<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            _text = text;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class FakeInteractionApi : IUserInteractionScriptApi
    {
        public string? Title { get; private set; }
        public ValueTask NotifyAsync(string title, string body, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<bool> ConfirmAsync(string title, string body, CancellationToken cancellationToken = default)
        {
            Title = title;
            return ValueTask.FromResult(true);
        }
    }
}
