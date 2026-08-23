using Diary.AiContext;
using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.PluginBase;
using Diary.ScriptHost;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Services;

public sealed record AiContextBuildOptions(
    bool IncludeTags = true,
    bool IncludeExtraFieldDefinitions = true,
    bool IncludeTemplates = true,
    bool IncludeTrackerInstances = true,
    bool IncludeSavedQueries = true,
    bool IncludeHostCapabilities = true,
    bool IncludeWorkItems = false,
    string? StartDate = null,
    string? EndDate = null,
    int MaxWorkItems = 50);

[DiAutoRegister(singleton: true)]
public sealed class AiContextSnapshotService(
    DbShareData shareData,
    PluginInstanceRegistry pluginInstances,
    ILogger<AiContextSnapshotService> logger)
{
    private static readonly string[] ReadOnlyHostCapabilities =
    [
        "workItems.query",
        "templates.list",
        "trackerInstances.get",
        "trackerInstances.list",
        "host.capabilities.list",
        "log.write",
        "script.progress",
    ];

    public string DefaultMcpSnapshotPath => Path.Combine(
        FsTools.GetApplicationConfigDirectory(), "ai-context", "mcp-snapshot.json");

    public AiContextSnapshot Build(AiContextBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxWorkItems is < 1 or > AiContextSchema.MaxWorkItems)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"事项数量必须在 1 到 {AiContextSchema.MaxWorkItems} 之间。");

        var database = App.Instance.UseDb
            ?? throw new InvalidOperationException("数据库尚未连接，无法生成 AI 上下文。");
        var tags = options.IncludeTags
            ? shareData.WorkTags.Select(tag => new AiContextTag(
                tag.Id, tag.Name, tag.Color, tag.Level.ToString(), tag.Disabled)).ToArray()
            : [];
        var fields = options.IncludeExtraFieldDefinitions
            ? database.GetAllTagExtraFieldDefinitions()
                .OrderBy(field => field.TagId)
                .ThenBy(field => field.SortOrder)
                .ThenBy(field => field.FieldKey, StringComparer.Ordinal)
                .Select(field => new AiContextExtraFieldDefinition(
                    field.FieldId, field.FieldKey, field.TagId, field.Label, field.Type.ToString(),
                    field.Description, field.SortOrder, field.Options.ToArray()))
                .ToArray()
            : [];
        var templates = options.IncludeTemplates
            ? TemplateManager.Instance.Templates
                .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                .Select(template => new AiContextTemplate(
                    template.Id, template.Name, template.DefaultTitle, template.DefaultTime,
                    template.DefaultWorkTags.ToArray()))
                .ToArray()
            : [];
        var trackers = options.IncludeTrackerInstances
            ? new TrackerInstanceScriptApi(pluginInstances).List()
                .Select(item => new AiContextTrackerInstance(
                    item.PluginId, item.InstanceId, item.DisplayName, item.Icon, item.IsConfigured))
                .ToArray()
            : [];
        var savedQueries = options.IncludeSavedQueries
            ? LoadSavedQueries()
            : [];
        var truncatedValueCount = 0;
        var workItems = options.IncludeWorkItems
            ? LoadWorkItems(options, out truncatedValueCount)
            : Array.Empty<AiContextWorkItem>();

        var disclosure = new AiContextDisclosure(
            options.IncludeTags,
            options.IncludeExtraFieldDefinitions,
            options.IncludeTemplates,
            options.IncludeTrackerInstances,
            options.IncludeSavedQueries,
            options.IncludeHostCapabilities,
            options.IncludeWorkItems);
        var sections = new List<string>();
        if (disclosure.Tags) sections.Add("tags");
        if (disclosure.ExtraFieldDefinitions) sections.Add("extra_field_definitions");
        if (disclosure.Templates) sections.Add("templates");
        if (disclosure.TrackerInstances) sections.Add("tracker_instances");
        if (disclosure.SavedQueries) sections.Add("saved_queries");
        if (disclosure.HostCapabilities) sections.Add("host_capabilities");
        if (disclosure.WorkItems) sections.Add("work_items");

        var snapshot = new AiContextSnapshot
        {
            Disclosure = disclosure,
            Tags = tags,
            ExtraFieldDefinitions = fields,
            Templates = templates,
            TrackerInstances = trackers,
            SavedQueries = savedQueries,
            HostCapabilities = options.IncludeHostCapabilities ? ReadOnlyHostCapabilities : [],
            WorkItems = workItems,
            Audit = new AiContextAudit(
                sections, tags.Length, fields.Length, templates.Length, trackers.Length,
                savedQueries.Length, workItems.Length, truncatedValueCount),
        };
        AiContextSerializer.Validate(snapshot);
        logger.LogInformation(
            "生成 AI 上下文快照 v{Version}：节 {Sections}，标签 {TagCount}，字段 {FieldCount}，模板 {TemplateCount}，Tracker {TrackerCount}，保存查询 {SavedQueryCount}，事项 {WorkItemCount}，截断 {TruncatedCount}",
            snapshot.SchemaVersion, string.Join(',', sections), tags.Length, fields.Length,
            templates.Length, trackers.Length, savedQueries.Length, workItems.Length, truncatedValueCount);
        return snapshot;
    }

    private AiContextSavedQuery[] LoadSavedQueries()
    {
        var store = new SavedWorkItemQueryStore(
            persistChanges: false, availableTags: shareData.WorkTags);
        return store.Queries.OrderBy(query => query.Name, StringComparer.OrdinalIgnoreCase)
            .Select(query => new AiContextSavedQuery(
                query.Id.ToString("D"), query.Name, query.StartDate, query.EndDate,
                (query.Tags ?? []).Select(tag => new AiContextSavedQueryTag(
                    tag.Id, tag.Name, tag.Level.ToString(), tag.Unresolved)).ToArray(),
                query.TagFilter.ToString(), query.Text, query.Priority is null ? null : (int)query.Priority.Value,
                query.Limit, query.Offset))
            .ToArray();
    }

    private static AiContextWorkItem[] LoadWorkItems(
        AiContextBuildOptions options,
        out int truncatedValueCount)
    {
        var database = App.Instance.UseDb!;
        var normalized = WorkItemQueryNormalizer.Normalize(new WorkItemQuery
        {
            StartDate = options.StartDate,
            EndDate = options.EndDate,
            Limit = options.MaxWorkItems,
        });
        var items = database.QueryWorkItems(normalized)
            .OrderBy(item => item.CreateDate, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .Take(options.MaxWorkItems)
            .ToArray();
        var ids = items.Select(item => item.Id).ToArray();
        var tags = database.GetWorkTagsByWorkItemIds(ids);
        var notes = database.GetWorkNotesByWorkItemIds(ids);
        var fields = database.GetWorkItemExtraFieldsByWorkItemIds(ids);
        var truncated = 0;
        var result = items.Select(item => new AiContextWorkItem(
            item.Id,
            item.CreateDate,
            Truncate(item.Comment, AiContextSchema.MaxTitleLength, ref truncated),
            item.Time,
            (int)item.Priority,
            notes.TryGetValue(item.Id, out var note)
                ? Truncate(note, AiContextSchema.MaxNoteLength, ref truncated)
                : null,
            tags.TryGetValue(item.Id, out var itemTags)
                ? itemTags.Select(tag => tag.Id).Order().ToArray()
                : [],
            fields.TryGetValue(item.Id, out var itemFields)
                ? itemFields.OrderBy(field => field.SortOrder)
                    .Take(50)
                    .Select(field => new AiContextWorkItemExtraField(
                        field.FieldId, field.FieldKey, field.TagId, field.TagName, field.Label,
                        field.Type.ToString(),
                        Truncate(field.Value, AiContextSchema.MaxExtraFieldValueLength, ref truncated)))
                    .ToArray()
                : []))
            .ToArray();
        truncatedValueCount = truncated;
        return result;
    }

    private static string Truncate(string value, int maxLength, ref int count)
    {
        if (value.Length <= maxLength)
            return value;
        count++;
        return value[..maxLength] + "…[truncated]";
    }
}
