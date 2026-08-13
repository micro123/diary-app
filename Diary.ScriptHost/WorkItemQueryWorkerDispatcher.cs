using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class WorkItemQueryWorkerDispatcher(
    Func<IWorkItemQueryScriptApi> apiFactory,
    Func<ITrackerInstanceScriptApi>? trackerApiFactory = null,
    Func<ILogItemScriptApi>? logItemApiFactory = null,
    Func<ITemplateLogItemScriptApi>? templateLogItemApiFactory = null,
    Func<ITemplateScriptApi>? templateApiFactory = null,
    Func<IHostCapabilitiesScriptApi>? hostCapabilitiesApiFactory = null,
    Func<IClipboardScriptApi>? clipboardApiFactory = null,
    Func<IUserInteractionScriptApi>? interactionApiFactory = null,
    Func<string, ILogApi>? scriptLogApiFactory = null,
    Func<string, ScriptProgressUpdate, CancellationToken, ValueTask>? progressReporter = null) : IWorkerHostCallDispatcher
{
    public async ValueTask<WorkerHostResultPayload> DispatchAsync(
        string executionId,
        WorkerHostCallPayload call,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(call.Method, "trackerInstances.get", StringComparison.Ordinal)
            || string.Equals(call.Method, "trackerInstances.list", StringComparison.Ordinal))
            return await DispatchTrackerAsync(trackerApiFactory, call);
        if (string.Equals(call.Method, "templates.list", StringComparison.Ordinal))
            return await DispatchTemplates(templateApiFactory);
        if (string.Equals(call.Method, "host.capabilities.list", StringComparison.Ordinal))
            return await DispatchHostCapabilities(hostCapabilitiesApiFactory);
        if (string.Equals(call.Method, "logItems.create", StringComparison.Ordinal))
            return await DispatchLogItemAsync(logItemApiFactory, call, cancellationToken);
        if (string.Equals(call.Method, "templateLogItems.create", StringComparison.Ordinal))
            return await DispatchTemplateLogItemAsync(templateLogItemApiFactory, call, cancellationToken);
        if (string.Equals(call.Method, "clipboard.get", StringComparison.Ordinal)
            || string.Equals(call.Method, "clipboard.set", StringComparison.Ordinal))
            return await DispatchClipboardAsync(clipboardApiFactory, call, cancellationToken);
        if (string.Equals(call.Method, "ui.notify", StringComparison.Ordinal)
            || string.Equals(call.Method, "ui.confirm", StringComparison.Ordinal))
            return await DispatchInteractionAsync(interactionApiFactory, call, cancellationToken);
        if (string.Equals(call.Method, "log.write", StringComparison.Ordinal))
            return await DispatchScriptLogAsync(scriptLogApiFactory, executionId, call, cancellationToken);
        if (string.Equals(call.Method, "script.progress", StringComparison.Ordinal))
            return await DispatchProgressAsync(progressReporter, executionId, call, cancellationToken);
        if (!string.Equals(call.Method, "workItems.query", StringComparison.Ordinal))
            return new(false, Error: new("InvalidInput", "不支持的 Worker 宿主 API。"));
        try
        {
            var query = call.Params.Deserialize<ScriptWorkItemQuery>(WorkerProtocol.JsonOptions)
                ?? throw new JsonException();
            var result = await apiFactory().QueryAsync(query, cancellationToken);
            return new(true, JsonSerializer.SerializeToElement(result, WorkerProtocol.JsonOptions));
        }
        catch (OperationCanceledException)
        {
            return new(true, JsonSerializer.SerializeToElement(
                ScriptWorkItemQueryResult.Failure(ScriptQueryErrorCode.Cancelled, "查询已取消。"),
                WorkerProtocol.JsonOptions));
        }
        catch (JsonException)
        {
            return new(true, JsonSerializer.SerializeToElement(
                ScriptWorkItemQueryResult.Failure(ScriptQueryErrorCode.InvalidInput, "查询参数格式无效。"),
                WorkerProtocol.JsonOptions));
        }
        catch (Exception)
        {
            return new(true, JsonSerializer.SerializeToElement(
                ScriptWorkItemQueryResult.Failure(ScriptQueryErrorCode.ProviderFailure, "数据库查询失败。"),
                WorkerProtocol.JsonOptions));
        }
    }

    private static async ValueTask<WorkerHostResultPayload> DispatchProgressAsync(
        Func<string, ScriptProgressUpdate, CancellationToken, ValueTask>? reporter,
        string executionId,
        WorkerHostCallPayload call,
        CancellationToken cancellationToken)
    {
        if (reporter is null)
            return new(true, JsonSerializer.SerializeToElement(new { accepted = true }, WorkerProtocol.JsonOptions));
        try
        {
            var update = call.Params.Deserialize<ScriptProgressUpdate>(WorkerProtocol.JsonOptions)
                ?? throw new JsonException();
            await reporter(executionId, update, cancellationToken);
            return new(true, JsonSerializer.SerializeToElement(new { accepted = true }, WorkerProtocol.JsonOptions));
        }
        catch (JsonException)
        { return new(false, Error: new("InvalidInput", "进度参数格式无效。")); }
        catch (ArgumentException exception)
        { return new(false, Error: new("InvalidInput", exception.Message)); }
        catch (OperationCanceledException)
        { return new(false, Error: new("Cancelled", "进度报告已取消。")); }
        catch (Exception exception)
        { return new(false, Error: new("ProviderFailure", exception.Message)); }
    }

    private static async ValueTask<WorkerHostResultPayload> DispatchTemplateLogItemAsync(
        Func<ITemplateLogItemScriptApi>? factory, WorkerHostCallPayload call, CancellationToken cancellationToken)
    {
        if (factory is null)
            return new(true, JsonSerializer.SerializeToElement(
                ScriptLogItemResult.Failure(ScriptLogItemErrorCode.ProviderFailure, "模板日志宿主 API 未配置。"),
                WorkerProtocol.JsonOptions));
        try
        {
            var request = call.Params.Deserialize<ScriptTemplateLogItemRequest>(WorkerProtocol.JsonOptions) ?? throw new JsonException();
            var result = await factory().CreateAsync(request, cancellationToken);
            return new(true, JsonSerializer.SerializeToElement(result, WorkerProtocol.JsonOptions));
        }
        catch (JsonException)
        {
            return new(true, JsonSerializer.SerializeToElement(
                ScriptLogItemResult.Failure(ScriptLogItemErrorCode.InvalidInput, "模板日志参数格式无效。"),
                WorkerProtocol.JsonOptions));
        }
        catch (OperationCanceledException)
        {
            return new(true, JsonSerializer.SerializeToElement(
                ScriptLogItemResult.Failure(ScriptLogItemErrorCode.Cancelled, "记录已取消。"),
                WorkerProtocol.JsonOptions));
        }
        catch
        {
            return new(true, JsonSerializer.SerializeToElement(
                ScriptLogItemResult.Failure(ScriptLogItemErrorCode.ProviderFailure, "按模板创建记录失败。"),
                WorkerProtocol.JsonOptions));
        }
    }

    private static async ValueTask<WorkerHostResultPayload> DispatchLogItemAsync(
        Func<ILogItemScriptApi>? factory, WorkerHostCallPayload call,
        CancellationToken cancellationToken)
    {
        if (factory is null)
            return new(true, JsonSerializer.SerializeToElement(
                ScriptLogItemResult.Failure(ScriptLogItemErrorCode.ProviderFailure, "日志记录宿主 API 未配置。"),
                WorkerProtocol.JsonOptions));
        try
        {
            var request = call.Params.Deserialize<ScriptLogItemRequest>(WorkerProtocol.JsonOptions) ?? throw new JsonException();
            var result = await factory().CreateAsync(request, cancellationToken);
            return new(true, JsonSerializer.SerializeToElement(result, WorkerProtocol.JsonOptions));
        }
        catch (JsonException)
        {
            return new(true, JsonSerializer.SerializeToElement(
                ScriptLogItemResult.Failure(ScriptLogItemErrorCode.InvalidInput, "日志记录参数格式无效。"),
                WorkerProtocol.JsonOptions));
        }
        catch (OperationCanceledException)
        {
            return new(true, JsonSerializer.SerializeToElement(
                ScriptLogItemResult.Failure(ScriptLogItemErrorCode.Cancelled, "记录已取消。"),
                WorkerProtocol.JsonOptions));
        }
        catch (Exception)
        {
            return new(true, JsonSerializer.SerializeToElement(
                ScriptLogItemResult.Failure(ScriptLogItemErrorCode.ProviderFailure, "创建日志记录失败。"),
                WorkerProtocol.JsonOptions));
        }
    }

    private static async ValueTask<WorkerHostResultPayload> DispatchClipboardAsync(
        Func<IClipboardScriptApi>? factory, WorkerHostCallPayload call, CancellationToken cancellationToken)
    {
        if (factory is null) return new(false, Error: new("ProviderFailure", "剪贴板宿主 API 未配置。"));
        try
        {
            var api = factory();
            if (call.Method == "clipboard.get")
                return new(true, JsonSerializer.SerializeToElement(await api.GetTextAsync(cancellationToken), WorkerProtocol.JsonOptions));
            var input = call.Params.Deserialize<ClipboardInput>(WorkerProtocol.JsonOptions) ?? throw new JsonException();
            await api.SetTextAsync(input.Text, cancellationToken);
            return new(true, JsonSerializer.SerializeToElement(true));
        }
        catch (JsonException) { return new(false, Error: new("InvalidInput", "剪贴板参数格式无效。")); }
        catch (OperationCanceledException) { return new(false, Error: new("Cancelled", "剪贴板操作已取消。")); }
        catch (Exception ex) { return new(false, Error: new("ProviderFailure", ex.Message)); }
    }

    private static async ValueTask<WorkerHostResultPayload> DispatchInteractionAsync(
        Func<IUserInteractionScriptApi>? factory, WorkerHostCallPayload call, CancellationToken cancellationToken)
    {
        if (factory is null) return new(false, Error: new("ProviderFailure", "用户交互宿主 API 未配置。"));
        try
        {
            var input = call.Params.Deserialize<InteractionInput>(WorkerProtocol.JsonOptions) ?? throw new JsonException();
            var api = factory();
            if (call.Method == "ui.notify") { await api.NotifyAsync(input.Title, input.Body, cancellationToken); return new(true); }
            return new(true, JsonSerializer.SerializeToElement(await api.ConfirmAsync(input.Title, input.Body, cancellationToken)));
        }
        catch (JsonException) { return new(false, Error: new("InvalidInput", "交互参数格式无效。")); }
        catch (OperationCanceledException) { return new(false, Error: new("Cancelled", "用户交互已取消。")); }
        catch (Exception ex) { return new(false, Error: new("ProviderFailure", ex.Message)); }
    }

    private static ValueTask<WorkerHostResultPayload> DispatchHostCapabilities(
        Func<IHostCapabilitiesScriptApi>? factory)
    {
        if (factory is null)
            return ValueTask.FromResult(new WorkerHostResultPayload(false, Error: new("ProviderFailure", "宿主能力发现 API 未配置。")));
        try
        {
            return ValueTask.FromResult(new WorkerHostResultPayload(
                true,
                JsonSerializer.SerializeToElement(factory().List(), WorkerProtocol.JsonOptions)));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(new WorkerHostResultPayload(false, Error: new("ProviderFailure", exception.Message)));
        }
    }

    private static ValueTask<WorkerHostResultPayload> DispatchTemplates(
        Func<ITemplateScriptApi>? factory)
    {
        if (factory is null)
            return ValueTask.FromResult(new WorkerHostResultPayload(false, Error: new("ProviderFailure", "模板发现宿主 API 未配置。")));
        return ValueTask.FromResult(new WorkerHostResultPayload(
            true,
            JsonSerializer.SerializeToElement(factory().List(), WorkerProtocol.JsonOptions)));
    }

    private static ValueTask<WorkerHostResultPayload> DispatchTrackerAsync(
        Func<ITrackerInstanceScriptApi>? factory,
        WorkerHostCallPayload call)
    {
        if (factory is null)
        {
            if (call.Method == "trackerInstances.list")
                return ValueTask.FromResult(new WorkerHostResultPayload(false, Error: new("ProviderFailure", "Tracker 宿主 API 未配置。")));
            return ValueTask.FromResult(new WorkerHostResultPayload(true,
                JsonSerializer.SerializeToElement(
                    TrackerScriptResult.Failure(TrackerScriptErrorCode.InstanceUnavailable, "Tracker 宿主 API 未配置。"),
                    WorkerProtocol.JsonOptions)));
        }
        try
        {
            if (call.Method == "trackerInstances.list")
                return ValueTask.FromResult(new WorkerHostResultPayload(
                    true,
                    JsonSerializer.SerializeToElement(factory().List(), WorkerProtocol.JsonOptions)));
            var request = call.Params.Deserialize<TrackerInstanceRequest>(WorkerProtocol.JsonOptions)
                ?? throw new JsonException();
            var result = factory().Get(request.PluginId, request.InstanceId);
            return ValueTask.FromResult(new WorkerHostResultPayload(
                true,
                JsonSerializer.SerializeToElement(result, WorkerProtocol.JsonOptions)));
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(new WorkerHostResultPayload(true,
                JsonSerializer.SerializeToElement(
                    TrackerScriptResult.Failure(TrackerScriptErrorCode.InvalidInput, "Tracker 实例参数格式无效。"),
                    WorkerProtocol.JsonOptions)));
        }
    }

    private static async ValueTask<WorkerHostResultPayload> DispatchScriptLogAsync(
        Func<string, ILogApi>? factory,
        string executionId,
        WorkerHostCallPayload call,
        CancellationToken cancellationToken)
    {
        if (factory is null)
            return new(false, Error: new("ProviderFailure", "脚本日志 API 未配置。"));
        try
        {
            var request = call.Params.Deserialize<ScriptLogRequest>(WorkerProtocol.JsonOptions)
                ?? throw new JsonException();
            var api = factory(executionId);
            switch (request.Level)
            {
                case ScriptLogLevel.Debug:
                    await api.DebugAsync(request.Message, cancellationToken);
                    break;
                case ScriptLogLevel.Info:
                    await api.InfoAsync(request.Message, cancellationToken);
                    break;
                case ScriptLogLevel.Warning:
                    await api.WarningAsync(request.Message, cancellationToken);
                    break;
                case ScriptLogLevel.Error:
                    await api.ErrorAsync(request.Message, cancellationToken);
                    break;
                default:
                    return new(false, Error: new("InvalidInput", "脚本日志级别无效。"));
            }
            return new(true);
        }
        catch (JsonException)
        {
            return new(false, Error: new("InvalidInput", "脚本日志参数格式无效。"));
        }
        catch (OperationCanceledException)
        {
            return new(false, Error: new("Cancelled", "脚本日志已取消。"));
        }
        catch (Exception exception)
        {
            return new(false, Error: new("ProviderFailure", exception.Message));
        }
    }

    private sealed record TrackerInstanceRequest(string PluginId, string InstanceId);
    private sealed record ClipboardInput(string Text);
    private sealed record InteractionInput(string Title, string Body);
    private sealed record ScriptLogRequest(ScriptLogLevel Level, string Message);
}
