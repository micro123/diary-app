using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed record ScriptLogItemRequest(
    string Date,
    double Hours,
    string Title,
    string? Note = null,
    string? IdempotencyKey = null,
    bool Preview = false);

public enum ScriptLogItemErrorCode
{
    InvalidInput = 1,
    DatabaseUnavailable = 2,
    ProviderFailure = 3,
    Cancelled = 4,
    PermissionDenied = 5,
}

public sealed record ScriptLogItemError(ScriptLogItemErrorCode Code, string Message)
{
    public ScriptApiError ToApiError() => Code switch
    {
        ScriptLogItemErrorCode.InvalidInput => new(ScriptApiErrorCodes.InvalidArgument, Message, ScriptErrorCategory.Validation),
        ScriptLogItemErrorCode.DatabaseUnavailable => new(ScriptApiErrorCodes.HostNotConfigured, Message, ScriptErrorCategory.Host, true),
        ScriptLogItemErrorCode.ProviderFailure => new("PROVIDER_FAILURE", Message, ScriptErrorCategory.Provider, true),
        ScriptLogItemErrorCode.Cancelled => new(ScriptApiErrorCodes.Cancelled, Message, ScriptErrorCategory.Cancellation),
        ScriptLogItemErrorCode.PermissionDenied => new(ScriptApiErrorCodes.ApiScopeNotSupported, Message, ScriptErrorCategory.Permission),
        _ => new("SCRIPT_LOG_ITEM_FAILED", Message, ScriptErrorCategory.Host),
    };
}

public sealed record ScriptLogItemResult(
    bool Succeeded,
    ScriptWorkItem? Item,
    ScriptLogItemError? Error)
{
    public ScriptEffectSummary? Effects { get; init; }
    public bool Duplicate { get; init; }
    public ScriptApiError? ApiError => Error?.ToApiError();

    public static ScriptLogItemResult Success(
        ScriptWorkItem item,
        ScriptEffectSummary? effects = null,
        bool duplicate = false) =>
        new(true, item, null) { Effects = effects, Duplicate = duplicate };

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
    string? Note = null,
    string? IdempotencyKey = null,
    bool Preview = false);

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
