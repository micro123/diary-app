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
        var trackerResult = result.Result!.Value.Deserialize<TrackerScriptResult>(WorkerProtocol.JsonOptions);
        Assert.IsTrue(trackerResult!.Succeeded);
        Assert.AreEqual("company", trackerResult.Instance!.InstanceId);
    }

    [TestMethod]
    public async Task DispatchAsync_ListsTemplatesAndTrackerInstances()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            trackerApiFactory: () => new FakeTrackerApi(),
            templateApiFactory: () => new FakeTemplateDiscoveryApi());

        var templates = await dispatcher.DispatchAsync("exec", new(
            "templates.list", JsonSerializer.SerializeToElement(new { })));
        var trackers = await dispatcher.DispatchAsync("exec", new(
            "trackerInstances.list", JsonSerializer.SerializeToElement(new { })));

        Assert.IsTrue(templates.Success);
        Assert.IsTrue(trackers.Success);
        Assert.AreEqual("日报", templates.Result!.Value.Deserialize<ScriptTemplateInfo[]>(WorkerProtocol.JsonOptions)![0].Name);
        Assert.AreEqual("default", trackers.Result!.Value.Deserialize<ScriptTrackerInstance[]>(WorkerProtocol.JsonOptions)![0].InstanceId);
    }

    [TestMethod]
    public async Task DispatchAsync_ListsHostCapabilities()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            hostCapabilitiesApiFactory: () => new FakeHostCapabilitiesApi());

        var result = await dispatcher.DispatchAsync("exec", new(
            "host.capabilities.list", JsonSerializer.SerializeToElement(new { })));

        Assert.IsTrue(result.Success);
        var capabilities = result.Result!.Value.Deserialize<string[]>(WorkerProtocol.JsonOptions);
        Assert.IsNotNull(capabilities);
        Assert.IsTrue(
            capabilities!.SequenceEqual(["host.capabilities.list", "workItems.query"]),
            string.Join(", ", capabilities));
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
    public async Task DispatchAsync_QueryEntryRejectsPersistentMutation()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            logItemApiFactory: () => new CapturingLogItemApi());
        var context = new ScriptHostCallContext(
            "exec", "worker", "query", ScriptEntryKind.Query, ScriptExecutionSource.Manual);

        var result = await dispatcher.DispatchAsync(context, new(
            "logItems.create",
            JsonSerializer.SerializeToElement(new ScriptLogItemRequest("2026-08-19", 1, "只读测试"))));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(ScriptApiErrorCodes.ApiScopeNotSupported, result.Error?.Code);
    }

    [TestMethod]
    public async Task DispatchAsync_PreviewForcesLogItemPreview()
    {
        var api = new CapturingLogItemApi();
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            logItemApiFactory: () => api);
        var context = new ScriptHostCallContext(
            "exec", "worker", "app", ScriptEntryKind.Application, ScriptExecutionSource.Manual, Preview: true);

        var result = await dispatcher.DispatchAsync(context, new(
            "logItems.create",
            JsonSerializer.SerializeToElement(new ScriptLogItemRequest("2026-08-19", 1, "预览测试"))));

        Assert.IsTrue(result.Success);
        Assert.IsTrue(api.Request?.Preview == true);
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
        var logItemResult = result.Result!.Value.Deserialize<ScriptLogItemResult>(WorkerProtocol.JsonOptions);
        Assert.IsTrue(logItemResult!.Succeeded);
        Assert.AreEqual("Title", logItemResult.Item!.Comment);
        Assert.AreEqual(2.5, logItemResult.Item.Hours);
    }

    [TestMethod]
    public async Task DispatchAsync_ReturnsLogItemWrapperWithEffectsAndDuplicate()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            logItemApiFactory: () => new EffectsLogItemApi());
        var result = await dispatcher.DispatchAsync("exec", new(
            "logItems.create",
            JsonSerializer.SerializeToElement(new ScriptLogItemRequest("2026-08-08", 1, "Title", IdempotencyKey: "key-1"))));

        Assert.IsTrue(result.Success);
        var roundTrip = result.Result!.Value.Deserialize<ScriptLogItemResult>(WorkerProtocol.JsonOptions);
        Assert.IsTrue(roundTrip!.Succeeded);
        Assert.IsTrue(roundTrip.Duplicate);
        Assert.IsNotNull(roundTrip.Effects);
        Assert.AreEqual(1, roundTrip.Effects!.AppendedCount);
        Assert.AreEqual(0, roundTrip.Item!.Id);
        Assert.AreEqual(7, roundTrip.Effects.CreatedWorkItemIds!.Single());
    }

    [TestMethod]
    public async Task DispatchAsync_ReturnsValueResultOnLogItemFailure()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            logItemApiFactory: () => new FailingLogItemApi());
        var result = await dispatcher.DispatchAsync("exec", new(
            "logItems.create",
            JsonSerializer.SerializeToElement(new ScriptLogItemRequest("2026-08-08", 1, "Title"))));

        Assert.IsTrue(result.Success);
        var roundTrip = result.Result!.Value.Deserialize<ScriptLogItemResult>(WorkerProtocol.JsonOptions);
        Assert.IsFalse(roundTrip!.Succeeded);
        Assert.IsNull(roundTrip.Item);
        Assert.AreEqual("InvalidInput", roundTrip.Error!.Code.ToString());
        Assert.AreEqual("INVALID_ARGUMENT", roundTrip.ApiError!.Code);
    }

    [TestMethod]
    public async Task DispatchAsync_ReturnsValueResultOnTrackerFailure()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            trackerApiFactory: () => new FailingTrackerApi());
        var result = await dispatcher.DispatchAsync("exec", new(
            "trackerInstances.get",
            JsonSerializer.SerializeToElement(new { pluginId = "tracker.memory", instanceId = "missing" })));

        Assert.IsTrue(result.Success);
        var roundTrip = result.Result!.Value.Deserialize<TrackerScriptResult>(WorkerProtocol.JsonOptions);
        Assert.IsFalse(roundTrip!.Succeeded);
        Assert.IsNull(roundTrip.Instance);
        Assert.AreEqual("InstanceUnavailable", roundTrip.ErrorCode!.Value.ToString());
        Assert.AreEqual("INSTANCE_UNAVAILABLE", roundTrip.ApiError!.Code);
    }

    [TestMethod]
    public async Task DispatchAsync_ReturnsValueResultOnQueryFailure()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(() => new FailingQueryApi());
        var result = await dispatcher.DispatchAsync("exec", new(
            "workItems.query", JsonSerializer.SerializeToElement(new ScriptWorkItemQuery { Limit = 0 })));

        Assert.IsTrue(result.Success);
        var roundTrip = result.Result!.Value.Deserialize<ScriptWorkItemQueryResult>(WorkerProtocol.JsonOptions);
        Assert.IsFalse(roundTrip!.Succeeded);
        Assert.AreEqual("InvalidInput", roundTrip.Error!.Code.ToString());
        Assert.AreEqual("INVALID_ARGUMENT", roundTrip.ApiError!.Code);
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

    [TestMethod]
    public async Task DispatchAsync_ReportsProgressToConfiguredReporter()
    {
        string? reportedExecutionId = null;
        ScriptProgressUpdate? reportedUpdate = null;
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            progressReporter: (executionId, update, _) =>
            {
                reportedExecutionId = executionId;
                reportedUpdate = update;
                return ValueTask.CompletedTask;
            });

        var result = await dispatcher.DispatchAsync("exec-progress", new(
            "script.progress", JsonSerializer.SerializeToElement(new { fraction = 0.6, message = "处理中" })));

        Assert.IsTrue(result.Success);
        Assert.AreEqual("exec-progress", reportedExecutionId);
        Assert.AreEqual(0.6, reportedUpdate!.Fraction);
        Assert.AreEqual("处理中", reportedUpdate.Message);
    }

    [TestMethod]
    public async Task DispatchAsync_ProgressWithoutReporterIsAccepted()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(() => new FakeQueryApi());

        var result = await dispatcher.DispatchAsync("exec-progress", new(
            "script.progress", JsonSerializer.SerializeToElement(new { fraction = 0.6, message = "处理中" })));

        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    public async Task DispatchAsync_RejectsInvalidProgressPayload()
    {
        var dispatcher = new WorkItemQueryWorkerDispatcher(
            () => new FakeQueryApi(),
            progressReporter: (_, _, _) => ValueTask.CompletedTask);

        var result = await dispatcher.DispatchAsync("exec-progress", new(
            "script.progress", JsonSerializer.SerializeToElement(new { fraction = "not-a-number" })));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("InvalidInput", result.Error!.Code);
    }

    private sealed class CapturingLogItemApi : ILogItemScriptApi
    {
        public ScriptLogItemRequest? Request { get; private set; }

        public ValueTask<ScriptLogItemResult> CreateAsync(
            ScriptLogItemRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(ScriptLogItemResult.Success(
                new ScriptWorkItem(1, request.Date, request.Title, request.Hours, 0, request.Note, [])));
        }
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

    private sealed class FakeTemplateDiscoveryApi : ITemplateScriptApi
    {
        public IReadOnlyList<ScriptTemplateInfo> List() =>
            [new("template-id", "日报", "日报标题", 1.5, [1, 2])];
    }

    private sealed class FakeHostCapabilitiesApi : IHostCapabilitiesScriptApi
    {
        public IReadOnlyList<string> List() => ["host.capabilities.list", "workItems.query"];
    }

    private sealed class FakeTemplateLogItemApi : ITemplateLogItemScriptApi
    {
        public ValueTask<ScriptLogItemResult> CreateAsync(ScriptTemplateLogItemRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptLogItemResult.Success(new(1, request.Date, request.Title ?? "Template", request.Hours, 0, request.Note, [])));
    }

    private sealed class EffectsLogItemApi : ILogItemScriptApi
    {
        public ValueTask<ScriptLogItemResult> CreateAsync(ScriptLogItemRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptLogItemResult.Success(
                new(0, request.Date, request.Title, request.Hours, 0, request.Note, []),
                new ScriptEffectSummary(1, false, request.IdempotencyKey, [7]),
                duplicate: true));
    }

    private sealed class FailingLogItemApi : ILogItemScriptApi
    {
        public ValueTask<ScriptLogItemResult> CreateAsync(ScriptLogItemRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptLogItemResult.Failure(ScriptLogItemErrorCode.InvalidInput, "参数无效。"));
    }

    private sealed class FailingTrackerApi : ITrackerInstanceScriptApi
    {
        public TrackerScriptResult Get(string pluginId, string instanceId) =>
            TrackerScriptResult.Failure(TrackerScriptErrorCode.InstanceUnavailable, "实例不存在。");

        public IReadOnlyList<ScriptTrackerInstance> List() => [];
    }

    private sealed class FailingQueryApi : IWorkItemQueryScriptApi
    {
        public ValueTask<ScriptWorkItemQueryResult> QueryAsync(ScriptWorkItemQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptWorkItemQueryResult.Failure(ScriptQueryErrorCode.InvalidInput, "Limit 无效。"));
    }

    private sealed class FakeTrackerApi : ITrackerInstanceScriptApi
    {
        public TrackerScriptResult Get(string pluginId, string instanceId) =>
            TrackerScriptResult.Success(new(pluginId, instanceId, "Company", "memory", true));

        public IReadOnlyList<ScriptTrackerInstance> List() =>
            [new("tracker", "default", "Company", "memory", true)];
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
