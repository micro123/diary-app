using System.Collections.Concurrent;
using System.Collections.Immutable;
using Diary.Core.Data.App;
using Diary.Database;
using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed class TemplateLogItemScriptApi(
    Func<DbInterfaceBase?> databaseProvider,
    Func<IReadOnlyCollection<Template>> templatesProvider) : ITemplateLogItemScriptApi
{
    private readonly ConcurrentDictionary<string, ScriptLogItemResult> _idempotentResults = new(StringComparer.Ordinal);
    public ValueTask<ScriptLogItemResult> CreateAsync(
        ScriptTemplateLogItemRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(Failure(ScriptLogItemErrorCode.Cancelled, "记录已取消。"));
        if (!TryValidate(request, out var error))
            return ValueTask.FromResult(Failure(ScriptLogItemErrorCode.InvalidInput, error));
        var template = templatesProvider().FirstOrDefault(item =>
            string.Equals(item.Id, request.TemplateId, StringComparison.OrdinalIgnoreCase));
        if (template is null)
            return ValueTask.FromResult(Failure(ScriptLogItemErrorCode.InvalidInput, "指定的模板不存在。"));
        var title = string.IsNullOrWhiteSpace(request.Title) ? template.DefaultTitle : request.Title;
        if (string.IsNullOrWhiteSpace(title) || title.Length > LogItemScriptApi.MaxTitleLength)
            return ValueTask.FromResult(Failure(ScriptLogItemErrorCode.InvalidInput, "标题不能为空或超过长度限制。"));
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey)
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
                0, request.Date, title, request.Hours, 0, request.Note,
                ImmutableArray<ScriptWorkTag>.Empty);
            return ValueTask.FromResult(ScriptLogItemResult.Success(
                previewItem,
                new ScriptEffectSummary(0, true, request.IdempotencyKey, [])));
        }

        DbInterfaceBase? database;
        try { database = databaseProvider(); }
        catch { return ValueTask.FromResult(Failure(ScriptLogItemErrorCode.ProviderFailure, "数据库提供程序不可用。")); }
        if (database is null)
            return ValueTask.FromResult(Failure(ScriptLogItemErrorCode.DatabaseUnavailable, "数据库尚未连接。"));

        var transactionStarted = false;
        var committed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            transactionStarted = database.BeginTransaction();
            if (!transactionStarted) throw new InvalidOperationException();
            var item = database.CreateWorkItem(request.Date, title);
            item.Time = request.Hours;
            if (!database.UpdateWorkItem(item)) throw new InvalidOperationException();
            var tags = database.AllWorkTags()
                .Where(tag => template.DefaultWorkTags.Contains(tag.Id)).ToArray();
            foreach (var tag in tags)
                if (!database.WorkItemAddTag(item, tag)) throw new InvalidOperationException();
            if (!string.IsNullOrWhiteSpace(request.Note)) database.WorkUpdateNote(item, request.Note);
            committed = database.CommitTransaction();
            if (!committed) throw new InvalidOperationException();
            var result = ScriptLogItemResult.Success(
                new ScriptWorkItem(
                    item.Id, item.CreateDate, item.Comment, item.Time, (int)item.Priority,
                    string.IsNullOrWhiteSpace(request.Note) ? null : request.Note,
                    [.. tags.Select(tag => new ScriptWorkTag(tag.Id, tag.Name, tag.Color, (int)tag.Level, tag.Disabled))]),
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
        { return ValueTask.FromResult(Failure(ScriptLogItemErrorCode.Cancelled, "记录已取消。")); }
        catch
        { return ValueTask.FromResult(Failure(ScriptLogItemErrorCode.ProviderFailure, "按模板创建记录失败。")); }
        finally
        {
            if (transactionStarted && !committed)
                try { database.RollbackTransaction(); } catch { }
        }
    }

    private static bool TryValidate(ScriptTemplateLogItemRequest? request, out string error)
    {
        error = string.Empty;
        if (request is null || !DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", out _))
            return Fail("日期必须是 yyyy-MM-dd 格式。", out error);
        if (!Guid.TryParse(request.TemplateId, out _))
            return Fail("模板 ID 必须是有效 UUID。", out error);
        if (double.IsNaN(request.Hours) || double.IsInfinity(request.Hours) || request.Hours <= 0 || request.Hours > 24)
            return Fail("耗时必须大于 0 且不超过 24 小时。", out error);
        if (request.Title?.Length > LogItemScriptApi.MaxTitleLength || request.Note?.Length > LogItemScriptApi.MaxNoteLength)
            return Fail("标题或备注超过长度限制。", out error);
        return true;
    }

    private static ScriptLogItemResult Failure(ScriptLogItemErrorCode code, string message) =>
        ScriptLogItemResult.Failure(code, message);
    private static bool Fail(string message, out string error) { error = message; return false; }
}
