using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class ScriptLogApi(Action<ScriptLogLevel, string> sink) : ILogApi
{
    public const int MaxMessageLength = 16 * 1024;

    public ValueTask DebugAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync(ScriptLogLevel.Debug, message, cancellationToken);

    public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync(ScriptLogLevel.Info, message, cancellationToken);

    public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync(ScriptLogLevel.Warning, message, cancellationToken);

    public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) =>
        WriteAsync(ScriptLogLevel.Error, message, cancellationToken);

    private ValueTask WriteAsync(
        ScriptLogLevel level,
        string message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        sink(level, message.Length <= MaxMessageLength
            ? message
            : message[..MaxMessageLength]);
        return ValueTask.CompletedTask;
    }
}
