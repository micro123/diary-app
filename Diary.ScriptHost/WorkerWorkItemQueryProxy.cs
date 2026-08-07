using System.Text.Json;
using Diary.Script.Runtime;

namespace Diary.ScriptHost;

public sealed class WorkerWorkItemQueryProxy(
    Func<WorkerHostCallPayload, CancellationToken, ValueTask<WorkerHostResultPayload>> callHost) : IWorkItemQueryScriptApi
{
    public async ValueTask<ScriptWorkItemQueryResult> QueryAsync(
        ScriptWorkItemQuery query,
        CancellationToken cancellationToken = default)
    {
        var response = await callHost(new(
            "workItems.query",
            JsonSerializer.SerializeToElement(query, WorkerProtocol.JsonOptions)), cancellationToken);
        if (response.Success && response.Result is { } result)
            return result.Deserialize<ScriptWorkItemQueryResult>(WorkerProtocol.JsonOptions)
                ?? ScriptWorkItemQueryResult.Failure(ScriptQueryErrorCode.ProviderFailure, "Worker 宿主返回了空查询结果。");

        var error = response.Error;
        return ScriptWorkItemQueryResult.Failure(error?.Code switch
        {
            "PermissionDenied" => ScriptQueryErrorCode.PermissionDenied,
            "InvalidInput" => ScriptQueryErrorCode.InvalidInput,
            "DatabaseUnavailable" => ScriptQueryErrorCode.DatabaseUnavailable,
            "Cancelled" => ScriptQueryErrorCode.Cancelled,
            _ => ScriptQueryErrorCode.ProviderFailure,
        }, error?.Message ?? "Worker 宿主查询失败。");
    }
}
