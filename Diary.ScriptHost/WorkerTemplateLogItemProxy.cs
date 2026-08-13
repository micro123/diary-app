using System.Text.Json;
using Diary.Script.Runtime;

namespace Diary.ScriptHost;

public sealed class WorkerTemplateLogItemProxy(
    Func<WorkerHostCallPayload, CancellationToken, ValueTask<WorkerHostResultPayload>> callHost)
    : ITemplateLogItemScriptApi
{
    public async ValueTask<ScriptLogItemResult> CreateAsync(
        ScriptTemplateLogItemRequest request, CancellationToken cancellationToken = default)
    {
        var response = await callHost(new("templateLogItems.create",
            JsonSerializer.SerializeToElement(request, WorkerProtocol.JsonOptions)), cancellationToken);
        if (!response.Success)
            return ScriptLogItemResult.Failure(ParseCode(response.Error?.Code), response.Error?.Message ?? "按模板创建记录失败。");
        return response.Result?.Deserialize<ScriptLogItemResult>(WorkerProtocol.JsonOptions)
            ?? ScriptLogItemResult.Failure(ScriptLogItemErrorCode.ProviderFailure, "宿主返回的结果为空。");
    }

    private static ScriptLogItemErrorCode ParseCode(string? code) =>
        Enum.TryParse<ScriptLogItemErrorCode>(code, out var parsed) ? parsed : ScriptLogItemErrorCode.ProviderFailure;
}
