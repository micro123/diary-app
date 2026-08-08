using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptBase;

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
        var item = response.Result?.Deserialize<ScriptWorkItem>(WorkerProtocol.JsonOptions);
        return item is null
            ? ScriptLogItemResult.Failure(ScriptLogItemErrorCode.ProviderFailure, "宿主返回的记录为空。")
            : ScriptLogItemResult.Success(item);
    }

    private static ScriptLogItemErrorCode ParseCode(string? code) =>
        Enum.TryParse<ScriptLogItemErrorCode>(code, out var parsed) ? parsed : ScriptLogItemErrorCode.ProviderFailure;
}
