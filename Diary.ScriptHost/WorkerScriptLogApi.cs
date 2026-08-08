using System.Text.Json;
using Diary.ScriptBase;
using Diary.Script.Runtime;

namespace Diary.ScriptHost;

public sealed class WorkerScriptLogApi(
    Func<WorkerHostCallPayload, CancellationToken, ValueTask<WorkerHostResultPayload>> callHost) : ILogApi
{
    public ValueTask DebugAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync(ScriptLogLevel.Debug, message, cancellationToken);

    public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync(ScriptLogLevel.Info, message, cancellationToken);

    public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync(ScriptLogLevel.Warning, message, cancellationToken);

    public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync(ScriptLogLevel.Error, message, cancellationToken);

    private async ValueTask WriteAsync(
        ScriptLogLevel level,
        string message,
        CancellationToken cancellationToken)
    {
        var response = await callHost(new(
            "log.write",
            JsonSerializer.SerializeToElement(new { level, message }, WorkerProtocol.JsonOptions)),
            cancellationToken);
        if (!response.Success)
            throw new InvalidOperationException(response.Error?.Message ?? "脚本日志写入失败。");
    }
}
