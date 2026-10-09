using System.Text.Json;
using Diary.Agent.Tools;
using Diary.ScriptHost;

namespace Diary.AgentTests;

[TestClass]
public sealed class ExpandedWriteToolTests
{
    [TestMethod]
    public async Task WorkItemUpdateRejectDoesNotWrite()
    {
        var api = new RecordingUpdateApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new WorkItemUpdateTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(UpdateArguments(), CreateContext()).AsTask();
        var request = await requested;
        Assert.AreEqual("旧标题", request.Arguments.GetProperty("before").GetProperty("Title").GetString());
        Assert.AreEqual("新标题", request.Arguments.GetProperty("after").GetProperty("Title").GetString());
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Reject);
        var result = await invocation;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("user_rejected", result.ErrorCode);
        Assert.AreEqual(0, api.UpdateCount);
    }

    [TestMethod]
    public async Task WorkItemUpdateConfirmWritesPreviewedCommand()
    {
        var api = new RecordingUpdateApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new WorkItemUpdateTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(UpdateArguments(), CreateContext()).AsTask();
        var request = await requested;
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Confirm);
        var result = await invocation;

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, api.UpdateCount);
        Assert.AreEqual("update-preview", api.LastUpdated?.PreviewVersion);
        StringAssert.Contains(result.EffectSummary!, "已更新事项 42");
    }

    [TestMethod]
    public async Task WorkItemUpdateConfirmationIncludesExtraFields()
    {
        var api = new RecordingUpdateApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new WorkItemUpdateTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(UpdateArguments(), CreateContext()).AsTask();
        var request = await requested;
        var extraField = request.Arguments
            .GetProperty("after")
            .GetProperty("ExtraFields")[0];

        Assert.AreEqual("field.project", extraField.GetProperty("FieldId").GetString());
        Assert.AreEqual("DiaryApp", extraField.GetProperty("Value").GetString());
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Reject);
        await invocation;
    }

    [TestMethod]
    public async Task WorkItemBatchUpdateRejectDoesNotWrite()
    {
        var api = new RecordingUpdateApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new WorkItemBatchUpdateTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(BatchUpdateArguments(), CreateContext()).AsTask();
        var request = await requested;

        Assert.AreEqual(2, request.Arguments.GetProperty("count").GetInt32());
        Assert.AreEqual(2, request.Arguments.GetProperty("items").GetArrayLength());
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Reject);
        var result = await invocation;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("user_rejected", result.ErrorCode);
        Assert.AreEqual(0, api.BatchUpdateCount);
    }

    [TestMethod]
    public async Task WorkItemBatchUpdateConfirmWritesPreviewedBatch()
    {
        var api = new RecordingUpdateApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new WorkItemBatchUpdateTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(BatchUpdateArguments(), CreateContext()).AsTask();
        var request = await requested;
        var items = request.Arguments.GetProperty("items");
        Assert.AreEqual("旧标题 42", items[0].GetProperty("before").GetProperty("Title").GetString());
        Assert.AreEqual("新标题 42", items[0].GetProperty("after").GetProperty("Title").GetString());
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Confirm);
        var result = await invocation;

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, api.BatchUpdateCount);
        Assert.AreEqual("batch-preview", api.LastBatchUpdated?.PreviewVersion);
        Assert.AreEqual(2, api.LastBatchUpdated?.Updates.Count);
        StringAssert.Contains(result.EffectSummary!, "已批量更新 2 个事项");
    }

    [TestMethod]
    public async Task ReportExportRejectDoesNotWriteFile()
    {
        var api = new RecordingReportExportApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new ReportExportTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(ReportArguments(), CreateContext()).AsTask();
        var request = await requested;
        Assert.AreEqual(2, request.Arguments.GetProperty("ItemCount").GetInt32());
        Assert.AreEqual("DiaryApp AI 导出目录", request.Arguments.GetProperty("destination").GetString());
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Reject);
        var result = await invocation;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, api.ExportCount);
    }

    [TestMethod]
    public async Task ReportExportConfirmWritesPreviewedReport()
    {
        var api = new RecordingReportExportApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new ReportExportTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(ReportArguments(), CreateContext()).AsTask();
        var request = await requested;
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Confirm);
        var result = await invocation;

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, api.ExportCount);
        Assert.AreEqual("report-preview", api.LastExported?.PreviewVersion);
        StringAssert.Contains(result.EffectSummary!, "weekly.csv");
    }

    private static Task<ExternalToolConfirmationRequest> WaitForRequest(AgentConfirmationCoordinator coordinator)
    {
        var completion = new TaskCompletionSource<ExternalToolConfirmationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ExternalConfirmationRequested += (_, request) => completion.TrySetResult(request);
        return completion.Task;
    }

    private static JsonElement UpdateArguments() => Json("""
        {
          "workItemId":42,
          "title":"新标题",
          "hours":2.5,
          "tagIds":[1,2],
          "extraFields":[{"fieldId":"field.project","value":"DiaryApp"}],
          "idempotencyKey":"update-42"
        }
        """);

    private static JsonElement BatchUpdateArguments() => Json("""
        {
          "updates":[
            {"workItemId":42,"title":"新标题 42"},
            {"workItemId":43,"hours":3,"extraFields":[{"fieldId":"field.owner","value":"Alice"}]}
          ],
          "idempotencyKey":"batch-update-42-43"
        }
        """);

    private static JsonElement ReportArguments() => Json("""
        {
          "period":"week",
          "date":"2026-10-09",
          "formatId":"csv",
          "fileName":"weekly.csv",
          "idempotencyKey":"report-week-2026-10-09"
        }
        """);

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static AgentToolInvocationContext CreateContext() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        EmptyServiceProvider.Instance);

    private sealed class RecordingUpdateApi : IWorkItemCommandApi
    {
        public int UpdateCount { get; private set; }

        public WorkItemUpdateCommand? LastUpdated { get; private set; }

        public int BatchUpdateCount { get; private set; }

        public WorkItemBatchUpdateCommand? LastBatchUpdated { get; private set; }

        public ValueTask<WorkItemCommandPreview> PreviewCreateAsync(
            WorkItemCreateCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<WorkItemCommandResult> CreateAsync(
            WorkItemCreateCommand command,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<WorkItemUpdatePreview> PreviewUpdateAsync(
            WorkItemUpdateCommand command,
            CancellationToken cancellationToken = default)
        {
            var normalized = command with
            {
                Date = "2026-10-09",
                Title = command.Title ?? "旧标题",
                Hours = command.Hours ?? 1,
                Priority = command.Priority ?? 1,
                TagIds = command.TagIds ?? [1],
                PreviewVersion = "update-preview",
            };
            return ValueTask.FromResult(new WorkItemUpdatePreview(
                true,
                new WorkItemUpdateSnapshot(42, "2026-10-08", "旧标题", 1, 1, [1]),
                normalized,
                "update-preview"));
        }

        public ValueTask<WorkItemCommandResult> UpdateAsync(
            WorkItemUpdateCommand command,
            CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            LastUpdated = command;
            return ValueTask.FromResult(new WorkItemCommandResult(
                true,
                command.WorkItemId,
                false,
                command.PreviewVersion));
        }

        public ValueTask<WorkItemBatchUpdatePreview> PreviewBatchUpdateAsync(
            WorkItemBatchUpdateCommand command,
            CancellationToken cancellationToken = default)
        {
            var items = command.Updates.Select(update =>
            {
                var normalized = update with
                {
                    Date = "2026-10-09",
                    Title = update.Title ?? $"旧标题 {update.WorkItemId}",
                    Hours = update.Hours ?? 1,
                    Priority = update.Priority ?? 1,
                    TagIds = update.TagIds ?? [1],
                    PreviewVersion = $"update-preview-{update.WorkItemId}",
                };
                return new WorkItemUpdatePreview(
                    true,
                    new WorkItemUpdateSnapshot(
                        update.WorkItemId,
                        "2026-10-08",
                        $"旧标题 {update.WorkItemId}",
                        1,
                        1,
                        [1]),
                    normalized,
                    normalized.PreviewVersion);
            }).ToArray();
            var normalizedBatch = command with
            {
                Updates = items.Select(item => item.Command!).ToArray(),
                PreviewVersion = "batch-preview",
            };
            return ValueTask.FromResult(new WorkItemBatchUpdatePreview(
                true,
                normalizedBatch,
                items,
                "batch-preview"));
        }

        public ValueTask<WorkItemBatchUpdateResult> BatchUpdateAsync(
            WorkItemBatchUpdateCommand command,
            CancellationToken cancellationToken = default)
        {
            BatchUpdateCount++;
            LastBatchUpdated = command;
            return ValueTask.FromResult(new WorkItemBatchUpdateResult(
                true,
                command.Updates.Select(update => update.WorkItemId).ToArray(),
                false,
                command.PreviewVersion));
        }
    }

    private sealed class RecordingReportExportApi : IAgentReportExportApi
    {
        public int ExportCount { get; private set; }

        public AgentReportExportCommand? LastExported { get; private set; }

        public ValueTask<AgentReportExportPreview> PreviewAsync(
            AgentReportExportCommand command,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AgentReportExportPreview(
                true,
                command with { PreviewVersion = "report-preview" },
                "2026-10-05",
                "2026-10-11",
                2,
                3.5,
                "report-preview"));

        public ValueTask<AgentReportExportResult> ExportAsync(
            AgentReportExportCommand command,
            CancellationToken cancellationToken = default)
        {
            ExportCount++;
            LastExported = command;
            return ValueTask.FromResult(new AgentReportExportResult(
                true,
                command.FileName,
                2,
                false,
                command.PreviewVersion));
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
