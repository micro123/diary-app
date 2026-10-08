using System.Text.Json;
using Diary.Agent.Tools;
using Diary.ScriptHost;

namespace Diary.AgentTests;

[TestClass]
public sealed class WorkItemWriteToolTests
{
    [TestMethod]
    public async Task RejectionNeverCallsCreate()
    {
        var api = new RecordingCommandApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new WorkItemWriteTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(
            CreateArguments(),
            CreateContext()).AsTask();
        var request = await requested;
        confirmations.Complete(
            request.ConfirmationId,
            new WorkItemConfirmationResponse(AgentConfirmationDecision.Reject));
        var result = await invocation;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("user_rejected", result.ErrorCode);
        Assert.AreEqual(1, api.PreviewCount);
        Assert.AreEqual(0, api.CreateCount);
    }

    [TestMethod]
    public async Task ConfirmationCreatesUsingPreviewVersion()
    {
        var api = new RecordingCommandApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new WorkItemWriteTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(
            CreateArguments(),
            CreateContext()).AsTask();
        var request = await requested;
        confirmations.Complete(
            request.ConfirmationId,
            new WorkItemConfirmationResponse(AgentConfirmationDecision.Confirm));
        var result = await invocation;

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, api.CreateCount);
        Assert.AreEqual("preview-v1", api.LastCreated?.PreviewVersion);
        StringAssert.Contains(result.EffectSummary!, "已创建事项");
    }

    [TestMethod]
    public async Task EditAndConfirmRepreviewsEditedFieldsBeforeCreate()
    {
        var api = new RecordingCommandApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new WorkItemWriteTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(
            CreateArguments(),
            CreateContext()).AsTask();
        var request = await requested;
        confirmations.Complete(
            request.ConfirmationId,
            new WorkItemConfirmationResponse(
                AgentConfirmationDecision.EditAndConfirm,
                request.Command with { Title = "用户编辑后的标题", PreviewVersion = null }));
        var result = await invocation;

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(2, api.PreviewCount);
        Assert.AreEqual("用户编辑后的标题", api.LastCreated?.Title);
        Assert.AreEqual("preview-v2", api.LastCreated?.PreviewVersion);
    }

    [TestMethod]
    public async Task CancellationRejectsPendingConfirmationAndDoesNotWrite()
    {
        var api = new RecordingCommandApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new WorkItemWriteTool(api, confirmations);
        using var cancellation = new CancellationTokenSource();
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(
            CreateArguments(),
            CreateContext(),
            cancellation.Token).AsTask();
        await requested;
        cancellation.Cancel();
        var result = await invocation;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("user_rejected", result.ErrorCode);
        Assert.AreEqual(0, api.CreateCount);
        Assert.IsNull(confirmations.Current);
    }

    private static Task<WorkItemConfirmationRequest> WaitForRequest(AgentConfirmationCoordinator coordinator)
    {
        var completion = new TaskCompletionSource<WorkItemConfirmationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ConfirmationRequested += (_, request) => completion.TrySetResult(request);
        return completion.Task;
    }

    private static JsonElement CreateArguments()
    {
        using var document = JsonDocument.Parse("""
            {
              "date":"2026-10-08",
              "title":"AI 草稿",
              "hours":1.5,
              "priority":2,
              "tagIds":[1],
              "extraFields":[],
              "note":"仅在用户确认后写入",
              "idempotencyKey":"write-tool-test"
            }
            """);
        return document.RootElement.Clone();
    }

    private static AgentToolInvocationContext CreateContext() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        new EmptyServiceProvider());

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class RecordingCommandApi : IWorkItemCommandApi
    {
        public int PreviewCount { get; private set; }

        public int CreateCount { get; private set; }

        public WorkItemCreateCommand? LastCreated { get; private set; }

        public ValueTask<WorkItemCommandPreview> PreviewCreateAsync(
            WorkItemCreateCommand command,
            CancellationToken cancellationToken = default)
        {
            PreviewCount++;
            var version = $"preview-v{PreviewCount}";
            var previewCommand = command with { PreviewVersion = version };
            return ValueTask.FromResult(new WorkItemCommandPreview(
                true,
                previewCommand,
                version));
        }

        public ValueTask<WorkItemCommandResult> CreateAsync(
            WorkItemCreateCommand command,
            CancellationToken cancellationToken = default)
        {
            CreateCount++;
            LastCreated = command;
            return ValueTask.FromResult(new WorkItemCommandResult(
                true,
                42,
                false,
                command.PreviewVersion));
        }
    }
}
