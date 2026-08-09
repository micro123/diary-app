using System.Text.Json;
using Diary.Script.Runtime;

namespace Diary.ScriptHost;

public sealed class TrackerInstanceWorkerProxy(
    Func<WorkerHostCallPayload, CancellationToken, ValueTask<WorkerHostResultPayload>> callHost) : ITrackerInstanceScriptApi
{
    public TrackerScriptResult Get(string pluginId, string instanceId)
    {
        var response = callHost(new(
            "trackerInstances.get",
            JsonSerializer.SerializeToElement(new { pluginId, instanceId }, WorkerProtocol.JsonOptions)),
            CancellationToken.None).GetAwaiter().GetResult();
        if (response.Success && response.Result is { } result)
            return TrackerScriptResult.Success(result.Deserialize<ScriptTrackerInstance>(WorkerProtocol.JsonOptions)!);
        return TrackerScriptResult.Failure(
            Enum.TryParse<TrackerScriptErrorCode>(response.Error?.Code, out var code)
                ? code
                : TrackerScriptErrorCode.InstanceUnavailable,
            response.Error?.Message ?? "Worker 宿主查询失败。");
    }

    public IReadOnlyList<ScriptTrackerInstance> List()
    {
        var response = callHost(new(
            "trackerInstances.list",
            JsonSerializer.SerializeToElement(new { }, WorkerProtocol.JsonOptions)),
            CancellationToken.None).GetAwaiter().GetResult();
        if (!response.Success || response.Result is not { } result)
            return [];
        return result.Deserialize<ScriptTrackerInstance[]>(WorkerProtocol.JsonOptions) ?? [];
    }
}
