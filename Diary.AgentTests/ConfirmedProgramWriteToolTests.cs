using System.Collections.Immutable;
using System.Text.Json;
using Diary.Agent.Tools;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.AgentTests;

[TestClass]
public sealed class ConfirmedProgramWriteToolTests
{
    [TestMethod]
    public async Task TemplateCreateRejectDoesNotWrite()
    {
        var api = new RecordingTemplateApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new TemplateWorkItemWriteTool(api, new RecordingTemplateDiscovery(), confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(TemplateArguments(), CreateContext()).AsTask();
        var request = await requested;
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Reject);
        var result = await invocation;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("user_rejected", result.ErrorCode);
        Assert.AreEqual(1, api.PreviewCount);
        Assert.AreEqual(0, api.CreateCount);
    }

    [TestMethod]
    public async Task TemplateCreateConfirmRepreviewsAndWritesOnce()
    {
        var api = new RecordingTemplateApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new TemplateWorkItemWriteTool(api, new RecordingTemplateDiscovery(), confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(TemplateArguments(), CreateContext()).AsTask();
        var request = await requested;
        Assert.AreEqual("测试模板", request.Arguments.GetProperty("template").GetProperty("name").GetString());
        Assert.AreEqual(2, request.Arguments.GetProperty("template").GetProperty("defaultWorkTagIds").GetArrayLength());
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Confirm);
        var result = await invocation;

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(2, api.PreviewCount);
        Assert.AreEqual(1, api.CreateCount);
        Assert.AreEqual("template-write-test", api.LastCreate?.IdempotencyKey);
        StringAssert.Contains(result.EffectSummary!, "已从模板创建事项");
    }

    [TestMethod]
    public async Task ClipboardRejectDoesNotChangeClipboard()
    {
        var api = new RecordingClipboardApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new ClipboardWriteTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(Json("""{"text":"待复制内容"}"""), CreateContext()).AsTask();
        var request = await requested;
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Reject);
        var result = await invocation;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, api.SetCount);
    }

    [TestMethod]
    public async Task ClipboardConfirmWritesExpectedText()
    {
        var api = new RecordingClipboardApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new ClipboardWriteTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(Json("""{"text":"待复制内容"}"""), CreateContext()).AsTask();
        var request = await requested;
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Confirm);
        var result = await invocation;

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, api.SetCount);
        Assert.AreEqual("待复制内容", api.Text);
    }

    [TestMethod]
    public async Task NotificationRejectDoesNotDisplayNotification()
    {
        var api = new RecordingInteractionApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new AppNotificationWriteTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(
            Json("""{"title":"提醒","body":"日报已生成"}"""),
            CreateContext()).AsTask();
        var request = await requested;
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Reject);
        var result = await invocation;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, api.NotifyCount);
    }

    [TestMethod]
    public async Task NotificationConfirmDisplaysNotification()
    {
        var api = new RecordingInteractionApi();
        var confirmations = new AgentConfirmationCoordinator();
        var tool = new AppNotificationWriteTool(api, confirmations);
        var requested = WaitForRequest(confirmations);

        var invocation = tool.InvokeAsync(
            Json("""{"title":"提醒","body":"日报已生成"}"""),
            CreateContext()).AsTask();
        var request = await requested;
        confirmations.CompleteExternal(request.ConfirmationId, AgentConfirmationDecision.Confirm);
        var result = await invocation;

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, api.NotifyCount);
        Assert.AreEqual("提醒", api.Title);
        Assert.AreEqual("日报已生成", api.Body);
    }

    private static Task<ExternalToolConfirmationRequest> WaitForRequest(
        AgentConfirmationCoordinator coordinator)
    {
        var completion = new TaskCompletionSource<ExternalToolConfirmationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ExternalConfirmationRequested += (_, request) => completion.TrySetResult(request);
        return completion.Task;
    }

    private static JsonElement TemplateArguments() => Json("""
        {
          "date":"2026-10-09",
          "templateId":"11111111-1111-1111-1111-111111111111",
          "hours":1.5,
          "title":"模板事项",
          "note":"确认后写入",
          "idempotencyKey":"template-write-test"
        }
        """);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
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

    private sealed class RecordingTemplateApi : ITemplateLogItemScriptApi
    {
        public int PreviewCount { get; private set; }
        public int CreateCount { get; private set; }
        public ScriptTemplateLogItemRequest? LastCreate { get; private set; }

        public ValueTask<ScriptLogItemResult> CreateAsync(
            ScriptTemplateLogItemRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Preview)
                PreviewCount++;
            else
            {
                CreateCount++;
                LastCreate = request;
            }
            var item = new ScriptWorkItem(
                request.Preview ? 0 : 42,
                request.Date,
                request.Title ?? "模板默认标题",
                request.Hours,
                0,
                request.Note,
                ImmutableArray<ScriptWorkTag>.Empty);
            return ValueTask.FromResult(ScriptLogItemResult.Success(item));
        }
    }

    private sealed class RecordingTemplateDiscovery : ITemplateScriptApi
    {
        public IReadOnlyList<ScriptTemplateInfo> List() =>
        [
            new ScriptTemplateInfo(
                "11111111-1111-1111-1111-111111111111",
                "测试模板",
                "模板默认标题",
                1.5,
                [1, 2]),
        ];
    }

    private sealed class RecordingClipboardApi : IClipboardScriptApi
    {
        public int SetCount { get; private set; }
        public string? Text { get; private set; }

        public ValueTask<string?> GetTextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Text);

        public ValueTask<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            SetCount++;
            Text = text;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingInteractionApi : IUserInteractionScriptApi
    {
        public int NotifyCount { get; private set; }
        public string? Title { get; private set; }
        public string? Body { get; private set; }

        public ValueTask RequestMainWindowActivationAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask NotifyAsync(
            string title,
            string body,
            CancellationToken cancellationToken = default)
        {
            NotifyCount++;
            Title = title;
            Body = body;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> ConfirmAsync(
            string title,
            string body,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }
}
