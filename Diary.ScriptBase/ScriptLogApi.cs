namespace Diary.ScriptBase;

public enum ScriptLogLevel
{
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
}

public interface ILogApi
{
    ValueTask DebugAsync(string message, CancellationToken cancellationToken = default);
    ValueTask InfoAsync(string message, CancellationToken cancellationToken = default);
    ValueTask WarningAsync(string message, CancellationToken cancellationToken = default);
    ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default);
}
