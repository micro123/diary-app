namespace Diary.AiContext;

public static class AiContextSchema
{
    public const string Id = "diary.ai_context";
    public const int Version = 1;
    public const int MaxSnapshotBytes = 2 * 1024 * 1024;
    public const int MaxWorkItems = 100;
    public const int MaxQueryOffset = 10_000;
    public const int MaxTitleLength = 2_000;
    public const int MaxNoteLength = 4_000;
    public const int MaxExtraFieldValueLength = 2_000;
}

public sealed record AiContextDisclosure(
    bool Tags,
    bool ExtraFieldDefinitions,
    bool Templates,
    bool TrackerInstances,
    bool SavedQueries,
    bool HostCapabilities,
    bool WorkItems);

public sealed record AiContextBudgets(
    int MaxWorkItems,
    int MaxTitleLength,
    int MaxNoteLength,
    int MaxExtraFieldValueLength,
    int MaxSnapshotBytes);

public sealed record AiContextTag(int Id, string Name, int Color, string Level, bool Disabled);

public sealed record AiContextExtraFieldDefinition(
    string FieldId,
    string FieldKey,
    int TagId,
    string Label,
    string Type,
    string Description,
    int SortOrder,
    IReadOnlyList<string> Options);

public sealed record AiContextTemplate(
    string Id,
    string Name,
    string DefaultTitle,
    double DefaultHours,
    IReadOnlyList<int> DefaultWorkTagIds);

public sealed record AiContextTrackerInstance(
    string PluginId,
    string InstanceId,
    string DisplayName,
    string Icon,
    bool IsConfigured);

public sealed record AiContextSavedQueryTag(int Id, string Name, string Level, bool Unresolved);

public sealed record AiContextSavedQuery(
    string Id,
    string Name,
    string? StartDate,
    string? EndDate,
    IReadOnlyList<AiContextSavedQueryTag> Tags,
    string TagFilter,
    string? Text,
    int? Priority,
    int? Limit,
    int Offset);

public sealed record AiContextWorkItemExtraField(
    string FieldId,
    string FieldKey,
    int TagId,
    string TagName,
    string Label,
    string Type,
    string Value);

public sealed record AiContextWorkItem(
    int Id,
    string Date,
    string Title,
    double Hours,
    int Priority,
    string? Note,
    IReadOnlyList<int> TagIds,
    IReadOnlyList<AiContextWorkItemExtraField> ExtraFields,
    bool UntrustedUserContent = true);

public sealed record AiContextAudit(
    IReadOnlyList<string> IncludedSections,
    int TagCount,
    int ExtraFieldDefinitionCount,
    int TemplateCount,
    int TrackerInstanceCount,
    int SavedQueryCount,
    int WorkItemCount,
    int TruncatedValueCount);

public sealed record AiContextSnapshot
{
    public string SchemaId { get; init; } = AiContextSchema.Id;
    public int SchemaVersion { get; init; } = AiContextSchema.Version;
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required AiContextDisclosure Disclosure { get; init; }
    public AiContextBudgets Budgets { get; init; } = new(
        AiContextSchema.MaxWorkItems,
        AiContextSchema.MaxTitleLength,
        AiContextSchema.MaxNoteLength,
        AiContextSchema.MaxExtraFieldValueLength,
        AiContextSchema.MaxSnapshotBytes);
    public IReadOnlyList<AiContextTag> Tags { get; init; } = [];
    public IReadOnlyList<AiContextExtraFieldDefinition> ExtraFieldDefinitions { get; init; } = [];
    public IReadOnlyList<AiContextTemplate> Templates { get; init; } = [];
    public IReadOnlyList<AiContextTrackerInstance> TrackerInstances { get; init; } = [];
    public IReadOnlyList<AiContextSavedQuery> SavedQueries { get; init; } = [];
    public IReadOnlyList<string> HostCapabilities { get; init; } = [];
    public IReadOnlyList<AiContextWorkItem> WorkItems { get; init; } = [];
    public required AiContextAudit Audit { get; init; }
}

public sealed record AiContextWorkItemQuery(
    string? StartDate = null,
    string? EndDate = null,
    IReadOnlyList<int>? TagIds = null,
    string? Text = null,
    int? Priority = null,
    int Limit = 50,
    int Offset = 0);

public sealed record AiContextTagSummary(int TagId, string TagName, int Count, double TotalHours);

public sealed record AiContextWorkItemSummary(
    int Count,
    double TotalHours,
    IReadOnlyList<AiContextTagSummary> ByTag);
