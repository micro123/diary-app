using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.App.Services;

public sealed class WorkItemCommandApi(
    Func<DbInterfaceBase?> databaseProvider,
    IWorkItemPersistenceCoordinator persistence,
    IWorkItemCommandIdempotencyStore idempotencyStore,
    Action? databaseChanged = null,
    IWorkItemAutomationPublisher? automationPublisher = null,
    IWorkItemTagAutomationService? tagAutomation = null) : IWorkItemCommandApi
{
    private const int MaxTitleLength = 500;
    private const int MaxNoteLength = 10_000;
    private const int MaxIdempotencyKeyLength = 200;
    private const int MaxBatchUpdateCount = 20;

    public ValueTask<WorkItemCommandPreview> PreviewCreateAsync(
        WorkItemCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = TryGetDatabase(out var databaseError);
        if (database is null)
            return ValueTask.FromResult(FailedPreview("database_unavailable", databaseError));
        if (!TryNormalizeAndResolve(database, command, out var normalized, out _, out _, out var errorCode, out var error))
            return ValueTask.FromResult(FailedPreview(errorCode, error));
        var version = CreatePreviewVersion(database, normalized!);
        var previewCommand = normalized! with { PreviewVersion = version };
        return ValueTask.FromResult(new WorkItemCommandPreview(
            true,
            previewCommand,
            version));
    }

    public ValueTask<WorkItemCommandResult> CreateAsync(
        WorkItemCreateCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return ValueTask.FromResult(FailedResult("idempotency_key_required", "创建事项必须提供幂等键。"));
        using var lease = idempotencyStore.Acquire(command.IdempotencyKey);
        if (idempotencyStore.TryGet(command.IdempotencyKey, out var previous))
            return ValueTask.FromResult(previous with { Duplicate = true });

        var database = TryGetDatabase(out var databaseError);
        if (database is null)
            return ValueTask.FromResult(FailedResult("database_unavailable", databaseError));
        if (!TryNormalizeAndResolve(
                database,
                command,
                out var normalized,
                out var tags,
                out var extraFields,
                out var errorCode,
                out var error))
        {
            return ValueTask.FromResult(FailedResult(errorCode, error));
        }
        var currentVersion = CreatePreviewVersion(database, normalized!);
        if (string.IsNullOrWhiteSpace(command.PreviewVersion)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(command.PreviewVersion),
                Encoding.UTF8.GetBytes(currentVersion)))
        {
            return ValueTask.FromResult(FailedResult(
                "preview_stale",
                "事项预览已过期，请重新预览并确认。",
                currentVersion));
        }

        using var tagPreparation = PrepareTagAutomation(null, tags!, []);
        if (!tagPreparation.Succeeded)
            return ValueTask.FromResult(FailedResult(
                "tag_automation_failed",
                tagPreparation.Error ?? "应用标签自动化失败。",
                currentVersion));

        var saveResult = persistence.Save(database, new WorkItemSaveRequest(
            null,
            normalized!.Date,
            normalized.Title,
            normalized.Note ?? string.Empty,
            normalized.Hours,
            (WorkPriorities)normalized.Priority,
            tags!,
            extraFields!,
            tagPreparation.Extensions));
        if (!saveResult.Success || saveResult.WorkItem is null)
            return ValueTask.FromResult(FailedResult("persistence_failed", saveResult.Error ?? "创建事项失败。", currentVersion));

        var result = new WorkItemCommandResult(
            true,
            saveResult.WorkItem.Id,
            false,
            currentVersion);
        idempotencyStore.Save(normalized.IdempotencyKey, result);
        try { databaseChanged?.Invoke(); }
        catch { }
        PublishAutomation(ScriptAutomationTriggerKind.WorkItemCreated, saveResult.WorkItem);
        PublishAddedTags(saveResult.WorkItem, tags!, []);
        return ValueTask.FromResult(result);
    }

    public ValueTask<WorkItemUpdatePreview> PreviewUpdateAsync(
        WorkItemUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = TryGetDatabase(out var databaseError);
        if (database is null)
            return ValueTask.FromResult(FailedUpdatePreview("database_unavailable", databaseError));
        if (!TryNormalizeUpdate(
                database,
                command,
                out _,
                out var before,
                out var normalized,
                out _,
                out _,
                out var errorCode,
                out var error))
        {
            return ValueTask.FromResult(FailedUpdatePreview(errorCode, error));
        }
        var version = CreateUpdatePreviewVersion(database, before!, normalized!);
        return ValueTask.FromResult(new WorkItemUpdatePreview(
            true,
            before,
            normalized! with { PreviewVersion = version },
            version));
    }

    public ValueTask<WorkItemCommandResult> UpdateAsync(
        WorkItemUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var idempotencyKey = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (idempotencyKey.Length is 0 or > MaxIdempotencyKeyLength)
            return ValueTask.FromResult(FailedResult("idempotency_key_required", "更新事项必须提供有效幂等键。"));
        using var lease = idempotencyStore.Acquire(idempotencyKey);
        if (idempotencyStore.TryGet(idempotencyKey, out var previous))
            return ValueTask.FromResult(previous with { Duplicate = true });

        var database = TryGetDatabase(out var databaseError);
        if (database is null)
            return ValueTask.FromResult(FailedResult("database_unavailable", databaseError));
        if (!TryNormalizeUpdate(
                database,
                command,
                out var existing,
                out var before,
                out var normalized,
                out var tags,
                out var extraFields,
                out var errorCode,
                out var error))
        {
            return ValueTask.FromResult(FailedResult(errorCode, error));
        }
        var currentVersion = CreateUpdatePreviewVersion(database, before!, normalized!);
        if (string.IsNullOrWhiteSpace(command.PreviewVersion)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(command.PreviewVersion),
                Encoding.UTF8.GetBytes(currentVersion)))
        {
            return ValueTask.FromResult(FailedResult(
                "preview_stale",
                "事项更新预览已过期，请重新预览并确认。",
                currentVersion));
        }

        using var tagPreparation = PrepareTagAutomation(existing, tags!, before!.TagIds);
        if (!tagPreparation.Succeeded)
            return ValueTask.FromResult(FailedResult(
                "tag_automation_failed",
                tagPreparation.Error ?? "应用标签自动化失败。",
                currentVersion));

        var saveResult = persistence.Save(database, new WorkItemSaveRequest(
            existing,
            normalized!.Date!,
            normalized.Title!,
            string.Empty,
            normalized.Hours!.Value,
            (WorkPriorities)normalized.Priority!.Value,
            tags!,
            extraFields!,
            tagPreparation.Extensions,
            PreserveNote: true));
        if (!saveResult.Success || saveResult.WorkItem is null)
            return ValueTask.FromResult(FailedResult("persistence_failed", saveResult.Error ?? "更新事项失败。", currentVersion));

        var result = new WorkItemCommandResult(
            true,
            saveResult.WorkItem.Id,
            false,
            currentVersion);
        idempotencyStore.Save(idempotencyKey, result);
        try { databaseChanged?.Invoke(); }
        catch { }
        PublishAutomation(ScriptAutomationTriggerKind.WorkItemSaved, saveResult.WorkItem);
        PublishAddedTags(saveResult.WorkItem, tags!, before!.TagIds);
        return ValueTask.FromResult(result);
    }

    public ValueTask<WorkItemBatchUpdatePreview> PreviewBatchUpdateAsync(
        WorkItemBatchUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var database = TryGetDatabase(out var databaseError);
        if (database is null)
            return ValueTask.FromResult(FailedBatchPreview("database_unavailable", databaseError));
        if (!TryNormalizeBatchUpdate(database, command, out var normalized, out var prepared, out var errorCode, out var error))
            return ValueTask.FromResult(FailedBatchPreview(errorCode, error));
        var version = CreateBatchUpdatePreviewVersion(database, prepared!);
        var itemPreviews = prepared!.Select(item =>
        {
            var itemVersion = CreateUpdatePreviewVersion(database, item.Before, item.Command);
            return new WorkItemUpdatePreview(
                true,
                item.Before,
                item.Command with { PreviewVersion = itemVersion },
                itemVersion);
        }).ToArray();
        return ValueTask.FromResult(new WorkItemBatchUpdatePreview(
            true,
            normalized! with { PreviewVersion = version },
            itemPreviews,
            version));
    }

    public ValueTask<WorkItemBatchUpdateResult> BatchUpdateAsync(
        WorkItemBatchUpdateCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var idempotencyKey = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (idempotencyKey.Length is 0 or > MaxIdempotencyKeyLength - 6)
            return ValueTask.FromResult(FailedBatchResult("idempotency_key_required", "批量更新必须提供有效幂等键。"));
        var storeKey = "batch:" + idempotencyKey;
        using var lease = idempotencyStore.Acquire(storeKey);
        if (idempotencyStore.TryGet(storeKey, out var previous))
        {
            return ValueTask.FromResult(new WorkItemBatchUpdateResult(
                previous.Succeeded,
                previous.WorkItemIds ?? [],
                true,
                previous.PreviewVersion,
                previous.ErrorCode,
                previous.ErrorMessage));
        }

        var database = TryGetDatabase(out var databaseError);
        if (database is null)
            return ValueTask.FromResult(FailedBatchResult("database_unavailable", databaseError));
        if (!TryNormalizeBatchUpdate(database, command, out _, out var prepared, out var errorCode, out var error))
            return ValueTask.FromResult(FailedBatchResult(errorCode, error));
        var preparedItems = prepared!;
        var currentVersion = CreateBatchUpdatePreviewVersion(database, preparedItems);
        if (string.IsNullOrWhiteSpace(command.PreviewVersion)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(command.PreviewVersion),
                Encoding.UTF8.GetBytes(currentVersion)))
        {
            return ValueTask.FromResult(FailedBatchResult(
                "preview_stale",
                "批量事项更新预览已过期，请重新预览并确认。",
                currentVersion));
        }

        var tagPreparations = new List<WorkItemTagAutomationPreparation>(preparedItems.Count);
        WorkItemBatchSaveResult saveResult;
        WorkItemSaveRequest[] requests;
        try
        {
            foreach (var item in preparedItems)
            {
                var preparation = PrepareTagAutomation(item.Existing, item.Tags, item.Before.TagIds);
                tagPreparations.Add(preparation);
                if (!preparation.Succeeded)
                {
                    return ValueTask.FromResult(FailedBatchResult(
                        "tag_automation_failed",
                        preparation.Error ?? $"应用事项 {item.Existing.Id} 的标签自动化失败。",
                        currentVersion));
                }
            }

            requests = preparedItems.Select((item, index) => new WorkItemSaveRequest(
                item.Existing,
                item.Command.Date!,
                item.Command.Title!,
                string.Empty,
                item.Command.Hours!.Value,
                (WorkPriorities)item.Command.Priority!.Value,
                item.Tags,
                item.ExtraFields,
                tagPreparations[index].Extensions,
                PreserveNote: true)).ToArray();
            saveResult = persistence.SaveBatch(database, requests);
        }
        finally
        {
            foreach (var preparation in tagPreparations)
                preparation.Dispose();
        }
        if (!saveResult.Success)
            return ValueTask.FromResult(FailedBatchResult("persistence_failed", saveResult.Error ?? "批量更新事项失败。", currentVersion));
        if (saveResult.Results.Count != requests.Length
            || saveResult.Results.Any(result => !result.Success || result.WorkItem is null))
        {
            return ValueTask.FromResult(FailedBatchResult(
                "persistence_failed",
                "批量更新返回的持久化结果不完整。",
                currentVersion));
        }

        var workItemIds = saveResult.Results
            .Select(result => result.WorkItem!.Id)
            .Order()
            .ToArray();
        var marker = new WorkItemCommandResult(
            true,
            null,
            false,
            currentVersion,
            WorkItemIds: workItemIds);
        idempotencyStore.Save(storeKey, marker);
        try { databaseChanged?.Invoke(); }
        catch { }
        for (var index = 0; index < saveResult.Results.Count; index++)
        {
            var savedItem = saveResult.Results[index].WorkItem!;
            var preparedItem = preparedItems[index];
            PublishAutomation(ScriptAutomationTriggerKind.WorkItemSaved, savedItem);
            PublishAddedTags(savedItem, preparedItem.Tags, preparedItem.Before.TagIds);
        }
        return ValueTask.FromResult(new WorkItemBatchUpdateResult(
            true,
            workItemIds,
            false,
            currentVersion));
    }

    private DbInterfaceBase? TryGetDatabase(out string error)
    {
        try
        {
            var database = databaseProvider();
            error = database is null ? "数据库尚未连接。" : string.Empty;
            return database;
        }
        catch
        {
            error = "数据库提供程序不可用。";
            return null;
        }
    }

    private WorkItemTagAutomationPreparation PrepareTagAutomation(
        WorkItem? existing,
        IReadOnlyCollection<WorkTag> desiredTags,
        IReadOnlyCollection<int> existingTagIds)
    {
        if (tagAutomation is null)
            return WorkItemTagAutomationPreparation.Empty;
        var existingIds = existingTagIds.ToHashSet();
        var addedTags = desiredTags
            .Where(tag => !existingIds.Contains(tag.Id))
            .OrderBy(tag => tag.Id)
            .ToArray();
        return tagAutomation.Prepare(existing, addedTags, TagAddSource.Agent);
    }

    private void PublishAddedTags(
        WorkItem item,
        IReadOnlyCollection<WorkTag> tags,
        IReadOnlyCollection<int> existingTagIds)
    {
        var existing = existingTagIds.ToHashSet();
        var sequence = 0;
        foreach (var tag in tags.Where(tag => !existing.Contains(tag.Id)).OrderBy(tag => tag.Id))
        {
            PublishAutomation(ScriptAutomationTriggerKind.TagAdded, item, tag, sequence);
            sequence++;
        }
    }

    private void PublishAutomation(
        ScriptAutomationTriggerKind trigger,
        WorkItem item,
        WorkTag? tag = null,
        int sequence = 0)
    {
        if (automationPublisher is null)
            return;
        try
        {
            automationPublisher.Publish(new WorkItemAutomationEvent(
                trigger,
                item.Id,
                item.CreateDate,
                item.Comment,
                item.Time,
                (int)item.Priority,
                tag?.Id,
                tag?.Name,
                tag is null ? null : (int)tag.Level,
                "Agent",
                sequence));
        }
        catch
        {
        }
    }

    private static bool TryNormalizeAndResolve(
        DbInterfaceBase database,
        WorkItemCreateCommand command,
        out WorkItemCreateCommand? normalized,
        out IReadOnlyCollection<WorkTag>? tags,
        out IReadOnlyCollection<WorkItemExtraFieldValue>? extraFields,
        out string errorCode,
        out string error)
    {
        normalized = null;
        tags = null;
        extraFields = null;
        errorCode = "invalid_command";
        error = string.Empty;
        if (!DateOnly.TryParseExact(command.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return Fail("日期必须是 yyyy-MM-dd 格式。", out error);
        var title = command.Title?.Trim() ?? string.Empty;
        if (title.Length is 0 or > MaxTitleLength)
            return Fail($"标题不能为空且不能超过 {MaxTitleLength} 个字符。", out error);
        if (!double.IsFinite(command.Hours) || command.Hours <= 0 || command.Hours > 24)
            return Fail("工时必须大于 0 且不超过 24 小时。", out error);
        if (!Enum.IsDefined((WorkPriorities)command.Priority))
            return Fail("优先级无效。", out error);
        if (command.Note?.Length > MaxNoteLength)
            return Fail($"备注不能超过 {MaxNoteLength} 个字符。", out error);
        var idempotencyKey = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (idempotencyKey.Length is 0 or > MaxIdempotencyKeyLength)
            return Fail($"幂等键不能为空且不能超过 {MaxIdempotencyKeyLength} 个字符。", out error);
        var tagIds = (command.TagIds ?? []).Distinct().Order().ToArray();
        if (tagIds.Length > 50)
            return Fail("单个事项不能包含超过 50 个标签。", out error);
        var availableTags = database.AllWorkTags().ToDictionary(tag => tag.Id);
        var resolvedTags = new List<WorkTag>(tagIds.Length);
        foreach (var tagId in tagIds)
        {
            if (!availableTags.TryGetValue(tagId, out var tag) || tag.Disabled)
            {
                errorCode = "tag_changed";
                return Fail($"标签 {tagId} 不存在或已停用。", out error);
            }
            resolvedTags.Add(tag);
        }

        var definitions = database.GetAllTagExtraFieldDefinitions()
            .Where(definition => tagIds.Contains(definition.TagId))
            .ToDictionary(definition => definition.FieldId, StringComparer.Ordinal);
        var resolvedFields = new List<WorkItemExtraFieldValue>();
        var seenFieldIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in command.ExtraFields ?? [])
        {
            if (!seenFieldIds.Add(field.FieldId))
                return Fail($"附加字段重复：{field.FieldId}。", out error);
            if (!definitions.TryGetValue(field.FieldId, out var definition) || !definition.Enabled)
            {
                errorCode = "extra_field_changed";
                return Fail($"附加字段 {field.FieldId} 不存在、未启用或不属于所选标签。", out error);
            }
            if (!TagExtraFieldValueValidator.TryValidate(definition.Type, field.Value, definition.Options, out var fieldError))
                return Fail($"附加字段“{definition.Label}”无效：{fieldError}", out error);
            resolvedFields.Add(new WorkItemExtraFieldValue
            {
                FieldId = field.FieldId,
                Value = field.Value,
            });
        }
        normalized = command with
        {
            Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Title = title,
            TagIds = tagIds,
            ExtraFields = (command.ExtraFields ?? [])
                .OrderBy(field => field.FieldId, StringComparer.Ordinal)
                .ToArray(),
            IdempotencyKey = idempotencyKey,
            PreviewVersion = null,
        };
        tags = resolvedTags;
        extraFields = resolvedFields;
        return true;
    }

    private static bool TryNormalizeUpdate(
        DbInterfaceBase database,
        WorkItemUpdateCommand command,
        out WorkItem? existing,
        out WorkItemUpdateSnapshot? before,
        out WorkItemUpdateCommand? normalized,
        out IReadOnlyCollection<WorkTag>? tags,
        out IReadOnlyCollection<WorkItemExtraFieldValue>? extraFields,
        out string errorCode,
        out string error)
    {
        existing = null;
        before = null;
        normalized = null;
        tags = null;
        extraFields = null;
        errorCode = "invalid_command";
        error = string.Empty;
        if (command.WorkItemId <= 0)
            return Fail("工作项 ID 必须是正整数。", out error);
        if (command.Date is null
            && command.Title is null
            && command.Hours is null
            && command.Priority is null
            && command.TagIds is null
            && command.ExtraFields is null)
        {
            return Fail("事项更新至少需要提供一个变更字段。", out error);
        }
        existing = database.QueryWorkItems(new WorkItemQuery
        {
            WorkItemId = command.WorkItemId,
            Limit = 1,
        }).SingleOrDefault();
        if (existing is null)
        {
            errorCode = "work_item_not_found";
            return Fail($"工作项 {command.WorkItemId} 不存在。", out error);
        }
        if (existing.IsReadOnly)
        {
            errorCode = "work_item_read_only";
            return Fail("迁移导入的只读工作项不能更新。", out error);
        }

        var currentTags = database.GetWorkItemTags(existing).OrderBy(tag => tag.Id).ToArray();
        var currentFields = database.GetWorkItemExtraFields(existing)
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .Select(field => new WorkItemExtraFieldCommand(field.FieldId, field.Value))
            .OrderBy(field => field.FieldId, StringComparer.Ordinal)
            .ToArray();
        before = new WorkItemUpdateSnapshot(
            existing.Id,
            existing.CreateDate,
            existing.Comment,
            existing.Time,
            (int)existing.Priority,
            currentTags.Select(tag => tag.Id).ToArray(),
            currentFields);
        var dateText = command.Date ?? existing.CreateDate;
        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return Fail("日期必须是 yyyy-MM-dd 格式。", out error);
        var title = command.Title is null ? existing.Comment : command.Title.Trim();
        if (title.Length is 0 or > MaxTitleLength)
            return Fail($"标题不能为空且不能超过 {MaxTitleLength} 个字符。", out error);
        var hours = command.Hours ?? existing.Time;
        if (!double.IsFinite(hours) || hours <= 0 || hours > 24)
            return Fail("工时必须大于 0 且不超过 24 小时。", out error);
        var priority = command.Priority ?? (int)existing.Priority;
        if (!Enum.IsDefined((WorkPriorities)priority))
            return Fail("优先级无效。", out error);
        var idempotencyKey = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (idempotencyKey.Length is 0 or > MaxIdempotencyKeyLength)
            return Fail($"幂等键不能为空且不能超过 {MaxIdempotencyKeyLength} 个字符。", out error);
        var tagIds = (command.TagIds ?? before.TagIds).Distinct().Order().ToArray();
        if (tagIds.Length > 50)
            return Fail("单个事项不能包含超过 50 个标签。", out error);
        var availableTags = database.AllWorkTags().ToDictionary(tag => tag.Id);
        var resolvedTags = new List<WorkTag>(tagIds.Length);
        foreach (var tagId in tagIds)
        {
            if (!availableTags.TryGetValue(tagId, out var tag) || tag.Disabled)
            {
                errorCode = "tag_changed";
                return Fail($"标签 {tagId} 不存在或已停用。", out error);
            }
            resolvedTags.Add(tag);
        }

        var definitions = database.GetAllTagExtraFieldDefinitions(includeDisabled: true)
            .Where(definition => tagIds.Contains(definition.TagId))
            .ToDictionary(definition => definition.FieldId, StringComparer.Ordinal);
        var finalFields = database.GetWorkItemExtraFields(existing)
            .Where(field => tagIds.Contains(field.TagId) && !string.IsNullOrWhiteSpace(field.Value))
            .ToDictionary(field => field.FieldId, field => field.Value, StringComparer.Ordinal);
        if (command.ExtraFields is not null)
        {
            var seenFieldIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in command.ExtraFields)
            {
                if (!seenFieldIds.Add(field.FieldId))
                    return Fail($"附加字段重复：{field.FieldId}。", out error);
                if (!definitions.TryGetValue(field.FieldId, out var definition) || !definition.Enabled)
                {
                    errorCode = "extra_field_changed";
                    return Fail($"附加字段 {field.FieldId} 不存在、未启用或不属于更新后的标签。", out error);
                }
                if (!TagExtraFieldValueValidator.TryValidate(definition.Type, field.Value, definition.Options, out var fieldError))
                    return Fail($"附加字段“{definition.Label}”无效：{fieldError}", out error);
                if (string.IsNullOrWhiteSpace(field.Value))
                    finalFields.Remove(field.FieldId);
                else
                    finalFields[field.FieldId] = field.Value;
            }
        }
        var normalizedFields = finalFields
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new WorkItemExtraFieldCommand(pair.Key, pair.Value))
            .ToArray();

        normalized = command with
        {
            Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Title = title,
            Hours = hours,
            Priority = priority,
            TagIds = tagIds,
            ExtraFields = normalizedFields,
            IdempotencyKey = idempotencyKey,
            PreviewVersion = null,
        };
        if (before.Date == normalized.Date
            && before.Title == normalized.Title
            && before.Hours == normalized.Hours
            && before.Priority == normalized.Priority
            && before.TagIds.SequenceEqual(normalized.TagIds!)
            && (before.ExtraFields ?? []).SequenceEqual(normalized.ExtraFields ?? []))
        {
            return Fail("事项更新没有产生实际变化。", out error);
        }
        tags = resolvedTags;
        var existingId = existing.Id;
        extraFields = normalizedFields
            .Select(field => new WorkItemExtraFieldValue
            {
                WorkItemId = existingId,
                FieldId = field.FieldId,
                Value = field.Value,
            })
            .ToArray();
        return true;
    }

    private static string CreatePreviewVersion(DbInterfaceBase database, WorkItemCreateCommand command)
    {
        var selectedTagIds = command.TagIds.ToHashSet();
        var schema = new
        {
            Command = command with { PreviewVersion = null },
            Tags = database.AllWorkTags()
                .Where(tag => selectedTagIds.Contains(tag.Id))
                .OrderBy(tag => tag.Id)
                .Select(tag => new { tag.Id, tag.Name, tag.Disabled, Level = (int)tag.Level }),
            Fields = database.GetAllTagExtraFieldDefinitions(includeDisabled: true)
                .Where(field => selectedTagIds.Contains(field.TagId))
                .OrderBy(field => field.FieldId, StringComparer.Ordinal)
                .Select(field => new
                {
                    field.FieldId,
                    field.TagId,
                    field.Type,
                    field.Enabled,
                    field.Options,
                }),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(schema);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string CreateUpdatePreviewVersion(
        DbInterfaceBase database,
        WorkItemUpdateSnapshot before,
        WorkItemUpdateCommand command)
    {
        var selectedTagIds = command.TagIds?.ToHashSet() ?? [];
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Before = before,
            Command = command with { PreviewVersion = null },
            Fields = database.GetAllTagExtraFieldDefinitions(includeDisabled: true)
                .Where(field => selectedTagIds.Contains(field.TagId))
                .OrderBy(field => field.FieldId, StringComparer.Ordinal)
                .Select(field => new
                {
                    field.FieldId,
                    field.TagId,
                    field.Type,
                    field.Enabled,
                    field.Options,
                }),
        });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string CreateBatchUpdatePreviewVersion(
        DbInterfaceBase database,
        IReadOnlyList<PreparedUpdate> prepared)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(prepared
            .OrderBy(item => item.Command.WorkItemId)
            .Select(item => new
            {
                item.Before,
                Command = item.Command with { PreviewVersion = null },
                Fields = database.GetAllTagExtraFieldDefinitions(includeDisabled: true)
                    .Where(field => item.Command.TagIds!.Contains(field.TagId))
                    .OrderBy(field => field.FieldId, StringComparer.Ordinal)
                    .Select(field => new
                    {
                        field.FieldId,
                        field.TagId,
                        field.Type,
                        field.Enabled,
                        field.Options,
                    }),
            }));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool TryNormalizeBatchUpdate(
        DbInterfaceBase database,
        WorkItemBatchUpdateCommand command,
        out WorkItemBatchUpdateCommand? normalized,
        out IReadOnlyList<PreparedUpdate>? prepared,
        out string errorCode,
        out string error)
    {
        normalized = null;
        prepared = null;
        errorCode = "invalid_command";
        error = string.Empty;
        var batchKey = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (batchKey.Length is 0 or > MaxIdempotencyKeyLength - 6)
            return Fail($"批量更新幂等键不能为空且不能超过 {MaxIdempotencyKeyLength - 6} 个字符。", out error);
        if (command.Updates.Count is 0 or > MaxBatchUpdateCount)
            return Fail($"单次批量更新必须包含 1 到 {MaxBatchUpdateCount} 个事项。", out error);
        if (command.Updates.Select(update => update.WorkItemId).Distinct().Count() != command.Updates.Count)
            return Fail("批量更新不能重复包含同一个事项。", out error);

        var items = new List<PreparedUpdate>(command.Updates.Count);
        for (var index = 0; index < command.Updates.Count; index++)
        {
            var update = command.Updates[index] with { IdempotencyKey = $"{batchKey}:{index + 1}" };
            if (!TryNormalizeUpdate(
                    database,
                    update,
                    out var existing,
                    out var before,
                    out var normalizedUpdate,
                    out var tags,
                    out var extraFields,
                    out errorCode,
                    out error))
            {
                error = $"事项 {update.WorkItemId}：{error}";
                return false;
            }
            items.Add(new PreparedUpdate(existing!, before!, normalizedUpdate!, tags!, extraFields!));
        }
        prepared = items;
        normalized = new WorkItemBatchUpdateCommand(
            items.Select(item => item.Command).ToArray(),
            batchKey);
        return true;
    }

    private static WorkItemCommandPreview FailedPreview(string code, string message) =>
        new(false, null, null, code, message);

    private static WorkItemUpdatePreview FailedUpdatePreview(string code, string message) =>
        new(false, null, null, null, code, message);

    private static WorkItemBatchUpdatePreview FailedBatchPreview(string code, string message) =>
        new(false, null, [], null, code, message);

    private static WorkItemCommandResult FailedResult(string code, string message, string? previewVersion = null) =>
        new(false, null, false, previewVersion, code, message);

    private static WorkItemBatchUpdateResult FailedBatchResult(
        string code,
        string message,
        string? previewVersion = null) =>
        new(false, [], false, previewVersion, code, message);

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private sealed record PreparedUpdate(
        WorkItem Existing,
        WorkItemUpdateSnapshot Before,
        WorkItemUpdateCommand Command,
        IReadOnlyCollection<WorkTag> Tags,
        IReadOnlyCollection<WorkItemExtraFieldValue> ExtraFields);
}
