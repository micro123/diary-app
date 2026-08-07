using System.Text.Json;
using Diary.Script.Runtime;

namespace Diary.ScriptHost;

public sealed class WorkerUserInteractionProxy(
    Func<WorkerHostCallPayload, CancellationToken, ValueTask<WorkerHostResultPayload>> callHost)
    : IUserInteractionScriptApi
{
    public async ValueTask NotifyAsync(string title, string body, CancellationToken cancellationToken = default)
    {
        var result = await callHost(new("ui.notify", JsonSerializer.SerializeToElement(new { title, body })), cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException(result.Error?.Message ?? "显示通知失败。");
    }

    public async ValueTask<bool> ConfirmAsync(string title, string body, CancellationToken cancellationToken = default)
    {
        var result = await callHost(new("ui.confirm", JsonSerializer.SerializeToElement(new { title, body })), cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException(result.Error?.Message ?? "显示确认对话框失败。");
        return result.Result?.GetBoolean() ?? false;
    }
}
