using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class WorkItemQueryWorkerDispatcher(
    Func<ScriptCapability, IWorkItemQueryScriptApi> apiFactory,
    Func<ScriptCapability, ITrackerInstanceScriptApi>? trackerApiFactory = null) : IWorkerHostCallDispatcher
{
    public async ValueTask<WorkerHostResultPayload> DispatchAsync(
        string executionId,
        ScriptCapability grantedCapabilities,
        WorkerHostCallPayload call,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(call.Method, "trackerInstances.get", StringComparison.Ordinal))
            return await DispatchTrackerAsync(trackerApiFactory, call);
        if (!string.Equals(call.Method, "workItems.query", StringComparison.Ordinal))
            return new(false, Error: new("InvalidInput", "不支持的 Worker 宿主 API。"));
        try
        {
            var query = call.Params.Deserialize<ScriptWorkItemQuery>(WorkerProtocol.JsonOptions)
                ?? throw new JsonException();
            var result = await apiFactory(grantedCapabilities).QueryAsync(query, cancellationToken);
            return result.Succeeded
                ? new(true, JsonSerializer.SerializeToElement(result, WorkerProtocol.JsonOptions))
                : new(false, Error: new(result.Error!.Code.ToString(), result.Error.Message));
        }
        catch (OperationCanceledException)
        {
            return new(false, Error: new("Cancelled", "查询已取消。"));
        }
        catch (JsonException)
        {
            return new(false, Error: new("InvalidInput", "查询参数格式无效。"));
        }
        catch (Exception)
        {
            return new(false, Error: new("ProviderFailure", "数据库查询失败。"));
        }
    }

    private static ValueTask<WorkerHostResultPayload> DispatchTrackerAsync(
        Func<ScriptCapability, ITrackerInstanceScriptApi>? factory,
        WorkerHostCallPayload call)
    {
        if (factory is null)
            return ValueTask.FromResult(new WorkerHostResultPayload(false, Error: new("ProviderFailure", "Tracker 宿主 API 未配置。")));
        try
        {
            var request = call.Params.Deserialize<TrackerInstanceRequest>(WorkerProtocol.JsonOptions)
                ?? throw new JsonException();
            var result = factory(ScriptCapabilities.All).Get(request.PluginId, request.InstanceId);
            return ValueTask.FromResult(result.Succeeded
                ? new WorkerHostResultPayload(true, JsonSerializer.SerializeToElement(result.Instance, WorkerProtocol.JsonOptions))
                : new WorkerHostResultPayload(false, Error: new(result.ErrorCode?.ToString() ?? "ProviderFailure", result.ErrorMessage ?? "Tracker 查询失败。")));
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(new WorkerHostResultPayload(false, Error: new("InvalidInput", "Tracker 实例参数格式无效。")));
        }
    }

    private sealed record TrackerInstanceRequest(string PluginId, string InstanceId);
}
