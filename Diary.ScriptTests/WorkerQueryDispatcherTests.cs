using System.Collections.Immutable;
using System.Text.Json;
using Diary.ScriptHost;
using Diary.Script.Runtime;
using Diary.ScriptBase;

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

    [TestMethod]
    public async Task DispatchAsync_TransportsLargeQueryResultWithinMessageLimit()
    {
        var items = Enumerable.Range(1, 1_000)
            .Select(id => new ScriptWorkItem(
                id,
                "2026-01-01",
                $"work item {id} {new string('x', 900)}",
                1.5,
                0,
                new string('n', 1_200),
                [new ScriptWorkTag(1, "large", 0, 0, false)]))
            .ToImmutableArray();
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FixedQueryApi(items));

        var result = await dispatcher.DispatchAsync("exec", new(
            "workItems.query", JsonSerializer.SerializeToElement(new ScriptWorkItemQuery { Limit = 1_000 })));

        Assert.IsTrue(result.Success);
        var payload = JsonSerializer.SerializeToUtf8Bytes(result, WorkerProtocol.JsonOptions);
        Assert.IsTrue(payload.Length > 1_000_000);
        Assert.IsTrue(payload.Length < WorkerProtocol.DefaultMaxMessageBytes);
        var roundTrip = result.Result!.Value.Deserialize<ScriptWorkItemQueryResult>(WorkerProtocol.JsonOptions);
        Assert.AreEqual(1_000, roundTrip!.Items.Length);
        Assert.AreEqual(new string('n', 1_200), roundTrip.Items[^1].Note);
    }

    [TestMethod]
    public async Task DispatchAsync_SupportsTemplateLogItemCreation()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            templateLogItemApiFactory: () => new FakeTemplateLogItemApi());
        var result = await dispatcher.DispatchAsync("exec", new(
            "templateLogItems.create",
            JsonSerializer.SerializeToElement(new ScriptTemplateLogItemRequest(
                "2026-08-08", "00000000-0000-0000-0000-000000000001", 2.5, "Title", "Note"))));

        Assert.IsTrue(result.Success);
        var item = result.Result!.Value.Deserialize<ScriptWorkItem>(WorkerProtocol.JsonOptions);
        Assert.AreEqual("Title", item!.Comment);
        Assert.AreEqual(2.5, item.Hours);
    }

    [TestMethod]
    public async Task DispatchAsync_ForwardsStructuredScriptLog()
    {
        string? executionId = null;
        var log = new FakeScriptLogApi();
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            scriptLogApiFactory: id =>
            {
                executionId = id;
                return log;
            });

        var result = await dispatcher.DispatchAsync("exec-log", new(
            "log.write", JsonSerializer.SerializeToElement(new { level = "Warning", message = "诊断" })));

        Assert.IsTrue(result.Success);
        Assert.AreEqual("exec-log", executionId);
        Assert.AreEqual((ScriptLogLevel.Warning, "诊断"), log.Last);
    }

    private sealed class FakeQueryApi : IWorkItemQueryScriptApi
    {
        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(ScriptWorkItemQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptWorkItemQueryResult.Success(ImmutableArray<ScriptWorkItem>.Empty, query));
    }

    private sealed class FixedQueryApi(ImmutableArray<ScriptWorkItem> items) : IWorkItemQueryScriptApi
    {
        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(ScriptWorkItemQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptWorkItemQueryResult.Success(items, query));
    }

    private sealed class FakeTemplateLogItemApi : ITemplateLogItemScriptApi
    {
        public ValueTask<ScriptLogItemResult> CreateAsync(ScriptTemplateLogItemRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptLogItemResult.Success(new(1, request.Date, request.Title ?? "Template", request.Hours, 0, request.Note, [])));
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

    private sealed class FakeScriptLogApi : ILogApi
    {
        public (ScriptLogLevel Level, string Message) Last { get; private set; }

        public ValueTask DebugAsync(string message, CancellationToken cancellationToken = default) => Write(ScriptLogLevel.Debug, message);
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => Write(ScriptLogLevel.Info, message);
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => Write(ScriptLogLevel.Warning, message);
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => Write(ScriptLogLevel.Error, message);

        private ValueTask Write(ScriptLogLevel level, string message)
        {
            Last = (level, message);
            return ValueTask.CompletedTask;
        }
    }
}
