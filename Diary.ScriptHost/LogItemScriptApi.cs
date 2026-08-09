using System.Collections.Concurrent;
using System.Collections.Immutable;
using Diary.Database;
using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class LogItemScriptApi(Func<DbInterfaceBase?> databaseProvider) : ILogItemScriptApi
{
    public const int MaxTitleLength = 500;
    public const int MaxNoteLength = 10_000;
    private readonly ConcurrentDictionary<string, ScriptLogItemResult> _idempotentResults = new(StringComparer.Ordinal);

    public ValueTask<ScriptLogItemResult> CreateAsync(
        ScriptLogItemRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(ScriptLogItemResult.Failure(ScriptLogItemErrorCode.Cancelled, "记录已取消。"));
        if (!TryValidate(request, out var error))
            return ValueTask.FromResult(ScriptLogItemResult.Failure(ScriptLogItemErrorCode.InvalidInput, error));
        if (!string.IsNullOrWhiteSpace(request!.IdempotencyKey)
            && _idempotentResults.TryGetValue(request.IdempotencyKey, out var previous))
        {
            return ValueTask.FromResult(previous with
            {
                Duplicate = true,
                Effects = previous.Effects is { } effects ? effects with { AppendedCount = 0 } : null,
            });
        }
        if (request.Preview)
        {
            var previewItem = new ScriptWorkItem(
                0, request.Date, request.Title, request.Hours, 0, request.Note,
                ImmutableArray<ScriptWorkTag>.Empty);
            return ValueTask.FromResult(ScriptLogItemResult.Success(
                previewItem,
                new ScriptEffectSummary(0, true, request.IdempotencyKey, [])));
        }

        DbInterfaceBase? database;
        try { database = databaseProvider(); }
        catch { return ValueTask.FromResult(ScriptLogItemResult.Failure(ScriptLogItemErrorCode.ProviderFailure, "数据库提供程序不可用。")); }
        if (database is null)
            return ValueTask.FromResult(ScriptLogItemResult.Failure(ScriptLogItemErrorCode.DatabaseUnavailable, "数据库尚未连接。"));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = database.CreateWorkItem(request!.Date, request.Title);
            item.Time = request.Hours;
            if (!database.UpdateWorkItem(item))
                return ValueTask.FromResult(ScriptLogItemResult.Failure(ScriptLogItemErrorCode.ProviderFailure, "保存记录失败。"));
            if (!string.IsNullOrWhiteSpace(request.Note))
                database.WorkUpdateNote(item, request.Note);
            var result = ScriptLogItemResult.Success(
                new ScriptWorkItem(
                    item.Id, item.CreateDate, item.Comment, item.Time, (int)item.Priority,
                    string.IsNullOrWhiteSpace(request.Note) ? null : request.Note,
                    ImmutableArray<ScriptWorkTag>.Empty),
                new ScriptEffectSummary(1, false, request.IdempotencyKey, [item.Id]));
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey)
                && !_idempotentResults.TryAdd(request.IdempotencyKey, result)
                && _idempotentResults.TryGetValue(request.IdempotencyKey, out var existing))
            {
                return ValueTask.FromResult(existing with
                {
                    Duplicate = true,
                    Effects = existing.Effects is { } effects ? effects with { AppendedCount = 0 } : null,
                });
            }
            return ValueTask.FromResult(result);
        }
        catch (OperationCanceledException)
        { return ValueTask.FromResult(ScriptLogItemResult.Failure(ScriptLogItemErrorCode.Cancelled, "记录已取消。")); }
        catch
        { return ValueTask.FromResult(ScriptLogItemResult.Failure(ScriptLogItemErrorCode.ProviderFailure, "保存记录失败。")); }
    }

    private static bool TryValidate(ScriptLogItemRequest? request, out string error)
    {
        error = string.Empty;
        if (request is null || !DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", out _))
            return Fail("日期必须是 yyyy-MM-dd 格式。", out error);
        if (double.IsNaN(request.Hours) || double.IsInfinity(request.Hours) || request.Hours <= 0 || request.Hours > 24)
            return Fail("耗时必须大于 0 且不超过 24 小时。", out error);
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitleLength)
            return Fail($"标题不能为空且不能超过 {MaxTitleLength} 个字符。", out error);
        if (request.Note?.Length > MaxNoteLength)
            return Fail($"备注不能超过 {MaxNoteLength} 个字符。", out error);
        return true;
    }

    private static bool Fail(string message, out string error) { error = message; return false; }
}
