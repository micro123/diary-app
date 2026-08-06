using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class WorkItemQueryWorkerDispatcher(
    Func<ScriptCapability, IWorkItemQueryScriptApi> apiFactory) : IWorkerHostCallDispatcher
{
    public async ValueTask<WorkerHostResultPayload> DispatchAsync(
        string executionId,
        ScriptCapability grantedCapabilities,
        WorkerHostCallPayload call,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(call.Method, "workItems.query", StringComparison.Ordinal))
            return new(false, Error: new("InvalidInput", "不支持的 Worker 宿主 API。"));
        if ((grantedCapabilities & ScriptCapability.ReadDiary) == 0)
            return new(false, Error: new("PermissionDenied", "脚本没有读取日记的权限。"));
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
}
