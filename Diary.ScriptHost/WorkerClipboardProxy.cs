using System.Text.Json;
using Diary.Script.Runtime;

namespace Diary.ScriptHost;

public sealed class WorkerClipboardProxy(
    Func<WorkerHostCallPayload, CancellationToken, ValueTask<WorkerHostResultPayload>> callHost)
    : IClipboardScriptApi
{
    public async ValueTask<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        var result = await callHost(new("clipboard.get", JsonSerializer.SerializeToElement(new { })), cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException(result.Error?.Message ?? "读取剪贴板失败。");
        return result.Result is null || result.Result.Value.ValueKind == JsonValueKind.Null
            ? null
            : result.Result.Value.Deserialize<string>(WorkerProtocol.JsonOptions);
    }

    public async ValueTask<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await callHost(new("clipboard.set", JsonSerializer.SerializeToElement(new { text })), cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException(result.Error?.Message ?? "写入剪贴板失败。");
        return true;
    }
}
