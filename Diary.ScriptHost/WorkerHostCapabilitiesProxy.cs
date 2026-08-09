using System.Text.Json;
using Diary.Script.Runtime;

namespace Diary.ScriptHost;

public sealed class WorkerHostCapabilitiesProxy(
    Func<WorkerHostCallPayload, CancellationToken, ValueTask<WorkerHostResultPayload>> callHost) : IHostCapabilitiesScriptApi
{
    public IReadOnlyList<string> List()
    {
        var response = callHost(
            new("host.capabilities.list", JsonSerializer.SerializeToElement(new { }, WorkerProtocol.JsonOptions)),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (!response.Success || response.Result is not { } result)
            return [];

        return result.Deserialize<string[]>(WorkerProtocol.JsonOptions) ?? [];
    }
}
