namespace Diary.ScriptHost;

public sealed record ScriptLogItemRequest(
    string Date,
    double Hours,
    string Title,
    string? Note = null);

public enum ScriptLogItemErrorCode
{
    InvalidInput = 1,
    DatabaseUnavailable = 2,
    ProviderFailure = 3,
    Cancelled = 4,
}

public sealed record ScriptLogItemError(ScriptLogItemErrorCode Code, string Message);

public sealed record ScriptLogItemResult(
    bool Succeeded,
    ScriptWorkItem? Item,
    ScriptLogItemError? Error)
{
    public static ScriptLogItemResult Success(ScriptWorkItem item) => new(true, item, null);
    public static ScriptLogItemResult Failure(ScriptLogItemErrorCode code, string message) =>
        new(false, null, new(code, message));
}

public interface ILogItemScriptApi
{
    ValueTask<ScriptLogItemResult> CreateAsync(
        ScriptLogItemRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ScriptTemplateLogItemRequest(
    string Date,
    string TemplateId,
    double Hours,
    string? Title = null,
    string? Note = null);

public interface ITemplateLogItemScriptApi
{
    ValueTask<ScriptLogItemResult> CreateAsync(
        ScriptTemplateLogItemRequest request,
        CancellationToken cancellationToken = default);
}

public interface IClipboardScriptApi
{
    ValueTask<string?> GetTextAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> SetTextAsync(string text, CancellationToken cancellationToken = default);
}

public interface IUserInteractionScriptApi
{
    ValueTask NotifyAsync(string title, string body, CancellationToken cancellationToken = default);
    ValueTask<bool> ConfirmAsync(string title, string body, CancellationToken cancellationToken = default);
}
