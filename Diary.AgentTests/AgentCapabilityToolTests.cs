using System.Text.Json;
using Diary.Agent.Configuration;
using Diary.Agent.Tools;
using Diary.ScriptHost;

namespace Diary.AgentTests;

[TestClass]
public sealed class AgentCapabilityToolTests
{
    [TestMethod]
    public void HighRiskPermissionGroupsAreDisabledByDefaultAndIndependentlyEnabled()
    {
        var defaults = new AiAgentToolSettings();
        Assert.IsTrue(AgentToolPermissionPolicy.IsEnabled(Descriptor(), defaults));

        foreach (var group in new[] { "WorkItemWrite", "ReportExport", "AppInteraction", "ReminderWrite", "TrackerSubmit", "ScriptExecute", "AgentMemoryWrite", "Mcp" })
        {
            Assert.IsFalse(AgentToolPermissionPolicy.IsEnabled(Descriptor(group), defaults), group);
            Assert.IsTrue(AgentToolPermissionPolicy.IsEnabled(Descriptor(group), Enable(group)), group);
        }
    }

    [TestMethod]
    public async Task ScheduleReminderRejectDoesNotPersist()
    {
        var api = new RecordingCapabilityApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new ScheduleReminderTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(Json("""
            {"title":"日报","body":"记得导出日报","dueAt":"2030-10-11T18:00:00+08:00"}
            """), CreateContext()).AsTask();
        var request = await requested;
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Reject);
        var result = await invocation;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("user_rejected", result.ErrorCode);
        Assert.AreEqual(0, api.ScheduleCount);
    }

    [TestMethod]
    public async Task ExecuteSavedScriptConfirmationContainsContractButNoSource()
    {
        var api = new RecordingCapabilityApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new ExecuteSavedScriptTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(Json("""
            {"scriptId":"weekly-summary","arguments":{"week":"2026-W41"},"idempotencyKey":"script-test"}
            """), CreateContext()).AsTask();
        var request = await requested;
        Assert.AreEqual("weekly-summary", request.Arguments.GetProperty("Id").GetString());
        Assert.AreEqual("2026-W41", request.Arguments.GetProperty("Arguments").GetProperty("week").GetString());
        Assert.IsFalse(request.Arguments.TryGetProperty("Source", out _));
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Confirm);
        var result = await invocation;

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, api.ExecuteCount);
    }

    [TestMethod]
    public async Task ExecuteSavedScriptRejectsNonReadyContractBeforeConfirmation()
    {
        var api = new RecordingCapabilityApi { ScriptState = "Invalid" };
        var tool = new ExecuteSavedScriptTool(api, new AgentConfirmationCoordinator());

        var result = await tool.InvokeAsync(Json("""
            {"scriptId":"weekly-summary","arguments":{},"idempotencyKey":"script-invalid"}
            """), CreateContext());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("script_not_ready", result.ErrorCode);
        Assert.AreEqual(0, api.ExecuteCount);
    }

    [TestMethod]
    public async Task TrackerSubmitUsesPreviewVersionAfterConfirmation()
    {
        var api = new RecordingCapabilityApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new SubmitTrackerTimeEntriesTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(Json("""
            {"workItemIds":[42],"idempotencyKey":"tracker-test"}
            """), CreateContext()).AsTask();
        var request = await requested;
        Assert.AreEqual("preview-v1", request.Arguments.GetProperty("PreviewVersion").GetString());
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Confirm);
        var result = await invocation;

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, api.PreviewCount);
        Assert.AreEqual(1, api.SubmitCount);
        Assert.AreEqual("preview-v1", api.SubmittedPreviewVersion);
    }

    [TestMethod]
    public async Task TrackerSubmitRejectsMoreThanTwentyItemsBeforePreview()
    {
        var api = new RecordingCapabilityApi();
        var tool = new SubmitTrackerTimeEntriesTool(api, new AgentConfirmationCoordinator());
        var ids = string.Join(',', Enumerable.Range(1, 21));

        var result = await tool.InvokeAsync(
            Json($$"""{"workItemIds":[{{ids}}],"idempotencyKey":"too-many"}"""),
            CreateContext());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("invalid_arguments", result.ErrorCode);
        Assert.AreEqual(0, api.PreviewCount);
    }

    private static AgentToolDescriptor Descriptor(string? permissionGroup = null) => new(
        "test.tool",
        "test_tool",
        "测试工具",
        "测试权限策略。",
        JsonSerializer.SerializeToElement(new { type = "object" }),
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.ReadOnly,
        "tests",
        permissionGroup);

    private static AiAgentToolSettings Enable(string group) => group switch
    {
        "WorkItemWrite" => new() { WorkItemWrite = true },
        "ReportExport" => new() { ReportExport = true },
        "AppInteraction" => new() { AppInteraction = true },
        "ReminderWrite" => new() { ReminderWrite = true },
        "TrackerSubmit" => new() { TrackerSubmit = true },
        "ScriptExecute" => new() { ScriptExecute = true },
        "AgentMemoryWrite" => new() { AgentMemoryWrite = true },
        "Mcp" => new() { Mcp = true },
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    private static Task<ExternalToolConfirmationRequest> WaitForRequest(AgentConfirmationCoordinator coordinator)
    {
        var completion = new TaskCompletionSource<ExternalToolConfirmationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ExternalConfirmationRequested += (_, request) => completion.TrySetResult(request);
        return completion.Task;
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AgentToolInvocationContext CreateContext() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        EmptyServiceProvider.Instance);

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }

    private sealed class RecordingCapabilityApi : IAgentCapabilityApi
    {
        public int ScheduleCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public int PreviewCount { get; private set; }
        public int SubmitCount { get; private set; }
        public string? SubmittedPreviewVersion { get; private set; }
        public string ScriptState { get; init; } = "Ready";

        public IReadOnlyList<AgentSavedQuery> ListSavedQueries() => [];

        public ValueTask<AgentSavedQueryResult> RunSavedQueryAsync(string queryId, int? limit = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AgentSavedQueryResult(false, null, []));

        public IReadOnlyList<AgentTrackerItem> SearchTrackerItems(string? pluginId, string? instanceId, string? query, int limit) => [];

        public AgentTrackerItem? GetTrackerItem(string pluginId, string instanceId, string itemId) => null;

        public IReadOnlyList<AgentScriptContract> ListScripts() => [GetScriptContract("weekly-summary")!];

        public AgentScriptContract? GetScriptContract(string scriptId) => scriptId == "weekly-summary"
            ? new AgentScriptContract(
                scriptId,
                "周报汇总",
                "生成本周汇总",
                "csharp",
                "Application",
                "Application",
                [],
                ScriptState,
                [])
            : null;

        public ValueTask<AgentScriptExecutionResult> ExecuteSavedScriptAsync(
            string scriptId,
            IReadOnlyDictionary<string, string> arguments,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return ValueTask.FromResult(new AgentScriptExecutionResult(true, scriptId, "Succeeded", [], null));
        }

        public IReadOnlyList<AgentReminder> ListReminders() => [];

        public ValueTask<AgentOperationResult> ScheduleReminderAsync(
            string title,
            string body,
            DateTimeOffset dueAt,
            CancellationToken cancellationToken = default)
        {
            ScheduleCount++;
            return ValueTask.FromResult(new AgentOperationResult(true, "reminder-1", Summary: "提醒已创建。"));
        }

        public ValueTask<AgentTrackerTimePreview> PreviewTrackerTimeEntriesAsync(
            IReadOnlyList<int> workItemIds,
            CancellationToken cancellationToken = default)
        {
            PreviewCount++;
            return ValueTask.FromResult(new AgentTrackerTimePreview(
                true,
                [new AgentTrackerTimeEntry(42, "2026-10-11", "实现 Agent", 1, "jira", "main", "Jira", "PROJ-42", "Pending", true)],
                1,
                "preview-v1"));
        }

        public ValueTask<AgentTrackerTimeSubmitResult> SubmitTrackerTimeEntriesAsync(
            IReadOnlyList<int> workItemIds,
            string idempotencyKey,
            string previewVersion,
            CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            SubmittedPreviewVersion = previewVersion;
            return ValueTask.FromResult(new AgentTrackerTimeSubmitResult(
                true,
                [new AgentOperationResult(true, "42")],
                false,
                previewVersion));
        }
    }
}
