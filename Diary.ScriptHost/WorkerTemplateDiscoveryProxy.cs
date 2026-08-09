using System.Text.Json;
using Diary.Script.Runtime;

namespace Diary.ScriptHost;

public sealed class WorkerTemplateDiscoveryProxy(
    Func<WorkerHostCallPayload, CancellationToken, ValueTask<WorkerHostResultPayload>> callHost) : ITemplateScriptApi
{
    public IReadOnlyList<ScriptTemplateInfo> List()
    {
        var response = callHost(new(
            "templates.list",
            JsonSerializer.SerializeToElement(new { }, WorkerProtocol.JsonOptions)),
            CancellationToken.None).GetAwaiter().GetResult();
        if (!response.Success || response.Result is not { } result)
            return [];
        return result.Deserialize<ScriptTemplateInfo[]>(WorkerProtocol.JsonOptions) ?? [];
    }
}
