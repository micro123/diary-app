using Diary.ScriptBase;

namespace Diary.ScriptHost;

public sealed record AgentSavedQuery(
    string Id,
    string Name,
    string? StartDate,
    string? EndDate,
    IReadOnlyList<int> TagIds,
    IReadOnlyList<string> TagNames,
    string TagFilter,
    string? Text,
    int? Priority,
    int? Limit,
    int Offset,
    bool HasUnresolvedTags);

public sealed record AgentSavedQueryResult(
    bool Succeeded,
    AgentSavedQuery? Query,
    IReadOnlyList<ScriptWorkItem> Items,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record AgentTrackerItem(
    string PluginId,
    string InstanceId,
    string InstanceName,
    string ItemId,
    string Title,
    string? Project,
    string? Status,
    bool Closed,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AgentScriptContract(
    string Id,
    string Name,
    string? Description,
    string Language,
    string Scope,
    string EntryKind,
    IReadOnlyList<ScriptParameterDefinition> Parameters,
    string State,
    IReadOnlyList<ScriptDiagnostic> Diagnostics);

public enum AgentReminderState
{
    Scheduled,
    Delivered,
    Missed,
    Cancelled,
}

public sealed record AgentReminder(
    string Id,
    string Title,
    string Body,
    DateTimeOffset DueAt,
    AgentReminderState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt = null);

public sealed record AgentOperationResult(
    bool Succeeded,
    string? Id = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? Summary = null);

public sealed record AgentScriptExecutionResult(
    bool Succeeded,
    string ScriptId,
    string Status,
    IReadOnlyList<ScriptDiagnostic> Diagnostics,
    ScriptEffectSummary? Effects,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record AgentTrackerTimeEntry(
    int WorkItemId,
    string Date,
    string Title,
    double Hours,
    string PluginId,
    string InstanceId,
    string InstanceName,
    string Target,
    string State,
    bool CanSubmit,
    string? ValidationError = null);

public sealed record AgentTrackerTimePreview(
    bool Succeeded,
    IReadOnlyList<AgentTrackerTimeEntry> Entries,
    double TotalHours,
    string? PreviewVersion,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record AgentTrackerTimeSubmitResult(
    bool Succeeded,
    IReadOnlyList<AgentOperationResult> Results,
    bool Duplicate,
    string? PreviewVersion,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public interface IAgentCapabilityApi
{
    IReadOnlyList<AgentSavedQuery> ListSavedQueries();

    ValueTask<AgentSavedQueryResult> RunSavedQueryAsync(
        string queryId,
        int? limit = null,
        CancellationToken cancellationToken = default);

    IReadOnlyList<AgentTrackerItem> SearchTrackerItems(
        string? pluginId,
        string? instanceId,
        string? query,
        int limit);

    AgentTrackerItem? GetTrackerItem(string pluginId, string instanceId, string itemId);

    IReadOnlyList<AgentScriptContract> ListScripts();

    AgentScriptContract? GetScriptContract(string scriptId);

    ValueTask<AgentScriptExecutionResult> ExecuteSavedScriptAsync(
        string scriptId,
        IReadOnlyDictionary<string, string> arguments,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    IReadOnlyList<AgentReminder> ListReminders();

    ValueTask<AgentOperationResult> ScheduleReminderAsync(
        string title,
        string body,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken = default);

    ValueTask<AgentTrackerTimePreview> PreviewTrackerTimeEntriesAsync(
        IReadOnlyList<int> workItemIds,
        CancellationToken cancellationToken = default);

    ValueTask<AgentTrackerTimeSubmitResult> SubmitTrackerTimeEntriesAsync(
        IReadOnlyList<int> workItemIds,
        string idempotencyKey,
        string previewVersion,
        CancellationToken cancellationToken = default);
}
