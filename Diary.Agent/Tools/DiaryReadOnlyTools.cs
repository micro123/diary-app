using System.Collections.Immutable;
using System.Text.Json;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.Agent.Tools;

public sealed record AgentWorkTagDto(int Id, string Name, int Color, int Level);

public sealed record AgentWorkItemExtraFieldDto(
    string FieldId,
    string FieldKey,
    int TagId,
    string TagName,
    string Label,
    string Type,
    string Value,
    string DefaultValue);

public sealed record AgentWorkItemDto(
    int Id,
    string Date,
    string Title,
    double Hours,
    int Priority,
    IReadOnlyList<AgentWorkTagDto> Tags,
    IReadOnlyList<AgentWorkItemExtraFieldDto> ExtraFields)
{
    public static AgentWorkItemDto FromScriptWorkItem(ScriptWorkItem item) => new(
        item.Id,
        item.Date,
        item.Comment,
        item.Hours,
        item.Priority,
        item.Tags.Select(tag => new AgentWorkTagDto(tag.Id, tag.Name, tag.Color, tag.Level)).ToArray(),
        item.ExtraFields.Select(field => new AgentWorkItemExtraFieldDto(
            field.FieldId,
            field.FieldKey,
            field.TagId,
            field.TagName,
            field.Label,
            field.Type.ToString(),
            field.Value,
            field.DefaultValue)).ToArray());
}

public static class DiaryReadOnlyToolRegistration
{
    public static IReadOnlyList<string> RegisterAll(
        AgentToolRegistry registry,
        IWorkItemQueryScriptApi workItems,
        ITemplateScriptApi templates,
        ITrackerInstanceScriptApi trackers,
        IWorkTagScriptApi tags,
        ICurrentContextScriptApi currentContext,
        IScriptValidationScriptApi scriptValidation,
        IExportApi? exports = null)
    {
        var tools = new List<IAgentTool>
        {
            new QueryWorkItemsTool(workItems),
            new GetWorkItemDetailTool(workItems),
            new SummarizeWorkItemsTool(workItems),
            new ListTemplatesTool(templates),
            new ListTrackerInstancesTool(trackers),
            new ListTagsTool(tags),
            new GetCurrentContextTool(currentContext),
            new ValidateScriptTool(scriptValidation),
        };
        if (exports is not null)
            tools.Add(new ListExportOptionsTool(exports));
        foreach (var tool in tools)
            registry.TryRegister(tool);
        return registry.RegistrationErrors;
    }
}

public sealed class QueryWorkItemsTool(IWorkItemQueryScriptApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.QueryWorkItems;

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseQuery(arguments, out var query, out var error))
            return AgentToolResult.Failure("invalid_arguments", error);
        var result = await api.QueryAsync(query, cancellationToken);
        if (!result.Succeeded)
            return AgentToolResult.Failure(result.ApiError?.Code ?? "query_failed", result.Error?.Message ?? "查询失败。");
        var items = result.Items.Select(AgentWorkItemDto.FromScriptWorkItem).ToArray();
        return AgentToolResult.Success(JsonSerializer.Serialize(new
        {
            items,
            count = items.Length,
            normalizedQuery = result.NormalizedQuery,
        }), "DiaryApp");
    }

    internal static bool TryParseQuery(
        JsonElement arguments,
        out ScriptWorkItemQuery query,
        out string error)
    {
        query = new ScriptWorkItemQuery();
        error = string.Empty;
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "参数必须是 JSON 对象。";
            return false;
        }
        try
        {
            var tagIds = arguments.TryGetProperty("tagIds", out var tagIdsElement)
                ? tagIdsElement.EnumerateArray().Select(item => item.GetInt32()).ToImmutableArray()
                : ImmutableArray<int>.Empty;
            var tagFilter = ScriptWorkItemTagFilter.Ignore;
            if (arguments.TryGetProperty("tagFilter", out var tagFilterElement)
                && !Enum.TryParse(tagFilterElement.GetString(), ignoreCase: true, out tagFilter))
            {
                error = "tagFilter 必须是 Ignore、Any、All、None 或 Exact。";
                return false;
            }
            var limit = TryReadNullableInt(arguments, "limit");
            if (limit is > 100)
            {
                error = "单次查询最多返回 100 个事项。";
                return false;
            }
            query = new ScriptWorkItemQuery
            {
                StartDate = TryReadString(arguments, "startDate"),
                WorkItemId = TryReadNullableInt(arguments, "workItemId"),
                EndDate = TryReadString(arguments, "endDate"),
                Range = TryReadString(arguments, "range"),
                Text = TryReadString(arguments, "text"),
                Priority = TryReadNullableInt(arguments, "priority"),
                Limit = limit ?? 50,
                Offset = TryReadNullableInt(arguments, "offset") ?? 0,
                TagIds = tagIds,
                TagFilter = tagFilter,
            };
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            error = "查询参数类型无效。";
            return false;
        }
    }

    private static string? TryReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static int? TryReadNullableInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt32()
            : null;
}

public sealed class GetWorkItemDetailTool(IWorkItemQueryScriptApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.GetWorkItemDetail;

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("workItemId", out var idElement)
            || !idElement.TryGetInt32(out var workItemId)
            || workItemId <= 0)
        {
            return AgentToolResult.Failure("invalid_arguments", "workItemId 必须是正整数。");
        }
        var result = await api.QueryAsync(
            new ScriptWorkItemQuery { WorkItemId = workItemId, Limit = 1 },
            cancellationToken);
        if (!result.Succeeded)
            return AgentToolResult.Failure(result.ApiError?.Code ?? "query_failed", result.Error?.Message ?? "查询失败。");
        if (result.Items.Length == 0)
            return AgentToolResult.Failure("work_item_not_found", $"工作项 {workItemId} 不存在。");
        return AgentToolResult.Success(
            JsonSerializer.Serialize(AgentWorkItemDto.FromScriptWorkItem(result.Items[0])),
            "DiaryApp");
    }
}

public sealed class SummarizeWorkItemsTool(IWorkItemQueryScriptApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.SummarizeWorkItems;

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!QueryWorkItemsTool.TryParseQuery(arguments, out var query, out var error))
            return AgentToolResult.Failure("invalid_arguments", error);
        var result = await api.QueryAsync(query, cancellationToken);
        if (!result.Succeeded)
            return AgentToolResult.Failure(result.ApiError?.Code ?? "query_failed", result.Error?.Message ?? "查询失败。");
        var summary = new
        {
            count = result.Items.Length,
            totalHours = result.Items.Sum(item => item.Hours),
            averageHours = result.Items.Length == 0 ? 0 : result.Items.Average(item => item.Hours),
            dateRange = result.Items.Length == 0
                ? null
                : new
                {
                    start = result.Items.Min(item => item.Date),
                    end = result.Items.Max(item => item.Date),
                },
            byDate = result.Items.GroupBy(item => item.Date).OrderBy(group => group.Key)
                .Select(group => new { date = group.Key, count = group.Count(), hours = group.Sum(item => item.Hours) }),
            byPriority = result.Items.GroupBy(item => item.Priority).OrderBy(group => group.Key)
                .Select(group => new { priority = group.Key, count = group.Count(), hours = group.Sum(item => item.Hours) }),
            byTag = result.Items.SelectMany(item => item.Tags).GroupBy(tag => new { tag.Id, tag.Name })
                .OrderByDescending(group => group.Count()).ThenBy(group => group.Key.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    id = group.Key.Id,
                    name = group.Key.Name,
                    count = group.Count(),
                    hours = result.Items.Where(item => item.Tags.Any(tag => tag.Id == group.Key.Id)).Sum(item => item.Hours),
                }),
        };
        return AgentToolResult.Success(JsonSerializer.Serialize(summary), "DiaryApp");
    }
}

public sealed class ListExportOptionsTool(IExportApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.ListExportOptions;

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        string? formatId = null;
        if (arguments.TryGetProperty("formatId", out var formatElement)
            && formatElement.ValueKind != JsonValueKind.Null)
        {
            if (formatElement.ValueKind != JsonValueKind.String)
                return AgentToolResult.Failure("invalid_arguments", "formatId 必须是字符串。");
            formatId = formatElement.GetString();
        }
        var formats = await api.ListFormatsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(formatId)
            && !formats.Any(item => string.Equals(item.FormatId, formatId, StringComparison.Ordinal)))
        {
            return AgentToolResult.Failure("export_format_not_found", $"导出格式 {formatId} 不存在。");
        }
        var templates = await api.ListTemplatesAsync(formatId, cancellationToken);
        return AgentToolResult.Success(JsonSerializer.Serialize(new { formats, templates }), "DiaryApp");
    }
}

public sealed class ListTemplatesTool(ITemplateScriptApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.ListTemplates;

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(api.List()), "DiaryApp"));
}

public sealed class ListTrackerInstancesTool(ITrackerInstanceScriptApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.ListTrackerInstances;

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(api.List()), "DiaryApp"));
}

public sealed class ListTagsTool(IWorkTagScriptApi source) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.ListTags;

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(source.List()), "DiaryApp"));
}

public sealed class GetCurrentContextTool(ICurrentContextScriptApi source) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.GetCurrentContext;

    public ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(source.Get()), "DiaryApp"));
}

public sealed class ValidateScriptTool(IScriptValidationScriptApi source) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = DiaryToolDescriptors.ValidateScript;

    public async ValueTask<AgentToolResult> InvokeAsync(
        JsonElement arguments,
        AgentToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("language", out var language)
            || !arguments.TryGetProperty("source", out var scriptSource)
            || language.ValueKind != JsonValueKind.String
            || scriptSource.ValueKind != JsonValueKind.String)
        {
            return AgentToolResult.Failure("invalid_arguments", "language 和 source 必须是字符串。");
        }
        var sourceText = scriptSource.GetString() ?? string.Empty;
        if (sourceText.Length > 256 * 1024)
            return AgentToolResult.Failure("script_too_large", "脚本源码超过大小限制。");
        var result = await source.ValidateAsync(language.GetString()!, sourceText, cancellationToken);
        return AgentToolResult.Success(JsonSerializer.Serialize(result), "DiaryApp");
    }
}

internal static class DiaryToolDescriptors
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement EmptySchema = Schema("{\"type\":\"object\",\"additionalProperties\":false}");

    private static readonly JsonElement QuerySchema = Schema("""
        {
          "type":"object",
          "properties":{
            "workItemId":{"type":"integer","minimum":1},
            "startDate":{"type":"string"},"endDate":{"type":"string"},"range":{"type":"string"},
            "text":{"type":"string"},"priority":{"type":"integer"},"limit":{"type":"integer","minimum":1,"maximum":100},
            "offset":{"type":"integer","minimum":0},"tagIds":{"type":"array","items":{"type":"integer"}},
            "tagFilter":{"type":"string","enum":["Ignore","Any","All","None","Exact"]}
          },
          "additionalProperties":false
        }
        """);

    public static AgentToolDescriptor QueryWorkItems { get; } = Create(
        "diary.query-work-items", "diary_query_work_items", "查询工作事项", "按日期、文本、优先级和标签查询 DiaryApp 工作事项。", QuerySchema);

    public static AgentToolDescriptor GetWorkItemDetail { get; } = Create(
        "diary.get-work-item-detail", "diary_get_work_item_detail", "读取事项详情", "按 ID 读取单个工作事项的非敏感详情，不包含本地备注。", Schema("""
            {"type":"object","properties":{"workItemId":{"type":"integer","minimum":1}},"required":["workItemId"],"additionalProperties":false}
            """));

    public static AgentToolDescriptor SummarizeWorkItems { get; } = Create(
        "diary.summarize-work-items", "diary_summarize_work_items", "汇总工作事项", "本地汇总工作事项数量、工时、日期和标签。", QuerySchema);

    public static AgentToolDescriptor ListTemplates { get; } = Create(
        "diary.list-templates", "diary_list_templates", "列出模板", "列出 DiaryApp 事项模板。", EmptySchema);

    public static AgentToolDescriptor ListTrackerInstances { get; } = Create(
        "diary.list-tracker-instances", "diary_list_tracker_instances", "列出 Tracker", "列出已启用的 Tracker 实例及配置状态。", EmptySchema);

    public static AgentToolDescriptor ListTags { get; } = Create(
        "diary.list-tags", "diary_list_tags", "列出标签", "列出 DiaryApp 工作标签。", EmptySchema);

    public static AgentToolDescriptor GetCurrentContext { get; } = Create(
        "diary.get-current-context", "diary_get_current_context", "读取当前上下文", "读取 UI 当前日期和选择事项的非敏感摘要。", EmptySchema);

    public static AgentToolDescriptor ValidateScript { get; } = Create(
        "diary.validate-script", "diary_validate_script", "校验脚本", "只校验脚本，不执行脚本。", Schema("""
            {"type":"object","properties":{"language":{"type":"string"},"source":{"type":"string"}},"required":["language","source"],"additionalProperties":false}
            """));

    public static AgentToolDescriptor ListExportOptions { get; } = Create(
        "diary.list-export-options", "diary_list_export_options", "列出导出选项", "列出可用的导出格式、能力和模板。", Schema("""
            {"type":"object","properties":{"formatId":{"type":["string","null"]}},"additionalProperties":false}
            """));

    private static AgentToolDescriptor Create(
        string id,
        string modelName,
        string displayName,
        string description,
        JsonElement schema) => new(
        id,
        modelName,
        displayName,
        description,
        schema,
        AgentToolOrigin.BuiltIn,
        AgentToolRisk.ReadOnly,
        "diary.ai-agent");
}
