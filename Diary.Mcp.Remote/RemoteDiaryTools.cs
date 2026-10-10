using System.Collections.Immutable;
using System.Text.Json;
using Diary.ScriptHost;

namespace Diary.Mcp.Remote;

public sealed class RemoteDiaryTools(
    IWorkItemQueryScriptApi workItems,
    IWorkTagScriptApi tags,
    ITagExtraFieldScriptApi extraFields,
    ICurrentContextScriptApi currentContext)
{
    public static IReadOnlyList<RemoteMcpToolDescriptor> Descriptors { get; } =
    [
        Descriptor("diary_list_tags", "列出标签", "列出 DiaryApp 标签和标签元数据。"),
        Descriptor("diary_list_extra_fields", "列出标签附加字段", "列出已启用的标签附加字段定义。"),
        Descriptor("diary_get_current_context", "读取当前上下文", "读取当前日期和当前选中事项的安全摘要。"),
        Descriptor("diary_query_work_items", "查询事项", "查询 DiaryApp 工作事项；不会返回本地备注。", QuerySchema()),
        Descriptor("diary_summarize_work_items", "汇总事项", "按条件汇总事项数量、工时和标签；不会返回本地备注。", SummarySchema()),
    ];

    public string ListTags() => Serialize(tags.List());

    public string ListExtraFields() => Serialize(extraFields.List());

    public string GetCurrentContext() => Serialize(currentContext.Get());

    public async Task<string> QueryWorkItemsAsync(
        string? startDate = null,
        string? endDate = null,
        int[]? tagIds = null,
        string? text = null,
        int? priority = null,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var result = await workItems.QueryAsync(new ScriptWorkItemQuery
        {
            StartDate = startDate,
            EndDate = endDate,
            TagIds = tagIds is null ? ImmutableArray<int>.Empty : [.. tagIds],
            TagFilter = tagIds is { Length: > 0 } ? ScriptWorkItemTagFilter.All : ScriptWorkItemTagFilter.Ignore,
            Text = text,
            Priority = priority,
            Limit = Math.Clamp(limit, 1, 1000),
            Offset = Math.Max(0, offset),
        }, cancellationToken);
        return Serialize(result);
    }

    public async Task<string> SummarizeWorkItemsAsync(
        string? startDate = null,
        string? endDate = null,
        int[]? tagIds = null,
        string? text = null,
        int? priority = null,
        CancellationToken cancellationToken = default)
    {
        var query = new ScriptWorkItemQuery
        {
            StartDate = startDate,
            EndDate = endDate,
            TagIds = tagIds is null ? ImmutableArray<int>.Empty : [.. tagIds],
            TagFilter = tagIds is { Length: > 0 } ? ScriptWorkItemTagFilter.All : ScriptWorkItemTagFilter.Ignore,
            Text = text,
            Priority = priority,
        };
        var count = 0;
        var totalHours = 0d;
        var byTag = new Dictionary<string, (int Count, double Hours)>(StringComparer.OrdinalIgnoreCase);
        await foreach (var item in workItems.StreamAsync(query, cancellationToken: cancellationToken))
        {
            count++;
            totalHours += item.Hours;
            foreach (var tag in item.Tags)
            {
                var current = byTag.GetValueOrDefault(tag.Name);
                byTag[tag.Name] = (current.Count + 1, current.Hours + item.Hours);
            }
        }
        return Serialize(new
        {
            count,
            totalHours,
            byTag = byTag.OrderByDescending(item => item.Value.Hours).Select(item => new
            {
                tag = item.Key,
                count = item.Value.Count,
                hours = item.Value.Hours,
            }),
        });
    }

    public Task<string> CallAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken) => toolName switch
        {
            "diary_list_tags" => Task.FromResult(ListTags()),
            "diary_list_extra_fields" => Task.FromResult(ListExtraFields()),
            "diary_get_current_context" => Task.FromResult(GetCurrentContext()),
            "diary_query_work_items" => QueryWorkItemsAsync(
                GetString(arguments, "startDate"),
                GetString(arguments, "endDate"),
                GetIntArray(arguments, "tagIds"),
                GetString(arguments, "text"),
                GetInt(arguments, "priority"),
                GetInt(arguments, "limit") ?? 100,
                GetInt(arguments, "offset") ?? 0,
                cancellationToken),
            "diary_summarize_work_items" => SummarizeWorkItemsAsync(
                GetString(arguments, "startDate"),
                GetString(arguments, "endDate"),
                GetIntArray(arguments, "tagIds"),
                GetString(arguments, "text"),
                GetInt(arguments, "priority"),
                cancellationToken),
            _ => throw new InvalidOperationException($"未知的只读 MCP 工具：{toolName}。"),
        };

    private static RemoteMcpToolDescriptor Descriptor(
        string name,
        string title,
        string description,
        JsonElement? inputSchema = null) => new(
        name,
        title,
        description,
        inputSchema ?? EmptySchema(),
        new RemoteMcpToolAnnotations(true, false, true, false));

    private static JsonElement EmptySchema() => ParseSchema("""{"type":"object","properties":{},"additionalProperties":false}""");

    private static JsonElement QuerySchema() => ParseSchema("""
        {"type":"object","properties":{"startDate":{"type":"string"},"endDate":{"type":"string"},"tagIds":{"type":"array","items":{"type":"integer"}},"text":{"type":"string"},"priority":{"type":"integer"},"limit":{"type":"integer","minimum":1,"maximum":1000},"offset":{"type":"integer","minimum":0}},"additionalProperties":false}
        """);

    private static JsonElement SummarySchema() => ParseSchema("""
        {"type":"object","properties":{"startDate":{"type":"string"},"endDate":{"type":"string"},"tagIds":{"type":"array","items":{"type":"integer"}},"text":{"type":"string"},"priority":{"type":"integer"}},"additionalProperties":false}
        """);

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? GetString(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private static int[]? GetIntArray(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetInt32()).ToArray()
            : null;

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
}

public sealed record RemoteMcpToolDescriptor(
    string Name,
    string Title,
    string Description,
    JsonElement InputSchema,
    RemoteMcpToolAnnotations Annotations);

public sealed record RemoteMcpToolAnnotations(
    bool ReadOnlyHint,
    bool DestructiveHint,
    bool IdempotentHint,
    bool OpenWorldHint);
