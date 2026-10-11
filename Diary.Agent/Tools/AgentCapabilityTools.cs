using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.ScriptHost;

namespace Diary.Agent.Tools;

public static class AgentCapabilityToolRegistration
{
    public static void RegisterAll(
        AgentToolRegistry registry,
        IAgentCapabilityApi api,
        IWorkItemCommandApi workItems,
        IAgentConfirmationService confirmations)
    {
        registry.TryRegister(new ListSavedQueriesTool(api));
        registry.TryRegister(new RunSavedQueryTool(api));
        registry.TryRegister(new SearchTrackerItemsTool(api));
        registry.TryRegister(new GetTrackerItemTool(api));
        registry.TryRegister(new ListScriptsTool(api));
        registry.TryRegister(new GetScriptContractTool(api));
        registry.TryRegister(new ListRemindersTool(api));
        registry.TryRegister(new ScheduleReminderTool(api, confirmations));
        registry.TryRegister(new ExecuteSavedScriptTool(api, confirmations));
        registry.TryRegister(new PreviewTrackerTimeEntriesTool(api));
        registry.TryRegister(new SubmitTrackerTimeEntriesTool(api, confirmations));
        registry.TryRegister(new BatchCreateWorkItemsTool(workItems, confirmations));
    }
}

internal static class AgentCapabilityToolSchemas
{
    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
    public static JsonElement Empty { get; } = Parse("""{"type":"object","properties":{},"additionalProperties":false}""");
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

public sealed class ListSavedQueriesTool(IAgentCapabilityApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.saved-queries.list", "diary_list_saved_queries", "列出保存查询",
        "列出用户在 DiaryApp 中保存的事项查询；不会修改或删除查询。",
        AgentCapabilityToolSchemas.Empty, AgentToolOrigin.BuiltIn, AgentToolRisk.ReadOnly, "diary.ai-agent");

    public ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(api.ListSavedQueries())));
}

public sealed class RunSavedQueryTool(IAgentCapabilityApi api) : IAgentTool
{
    private static readonly JsonElement Schema = AgentCapabilityToolSchemas.Parse("""
        {"type":"object","properties":{"savedQueryId":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":500}},"required":["savedQueryId"],"additionalProperties":false}
        """);
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.saved-queries.run", "diary_run_saved_query", "运行保存查询",
        "按保存查询 ID 读取事项。失效标签会拒绝运行，本地备注不会返回。",
        Schema, AgentToolOrigin.BuiltIn, AgentToolRisk.ReadOnly, "diary.ai-agent");

    public async ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("savedQueryId", out var id) || id.ValueKind != JsonValueKind.String)
            return AgentToolResult.Failure("invalid_arguments", "savedQueryId 不能为空。");
        var limit = arguments.TryGetProperty("limit", out var value) ? value.GetInt32() : (int?)null;
        var result = await api.RunSavedQueryAsync(id.GetString()!, limit, cancellationToken);
        return result.Succeeded
            ? AgentToolResult.Success(JsonSerializer.Serialize(result))
            : AgentToolResult.Failure(result.ErrorCode ?? "saved_query_failed", result.ErrorMessage ?? "保存查询运行失败。");
    }
}

public sealed class SearchTrackerItemsTool(IAgentCapabilityApi api) : IAgentTool
{
    private static readonly JsonElement Schema = AgentCapabilityToolSchemas.Parse("""
        {"type":"object","properties":{"pluginId":{"type":["string","null"]},"instanceId":{"type":["string","null"]},"query":{"type":["string","null"]},"limit":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
        """);
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.tracker.search", "diary_search_tracker_items", "搜索 Tracker 事项",
        "只搜索 Jira/Redmine 已同步到本地的非敏感目录，不访问远程服务。",
        Schema, AgentToolOrigin.BuiltIn, AgentToolRisk.ReadOnly, "diary.ai-agent");

    public ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        var input = arguments.Deserialize<SearchInput>(AgentCapabilityToolSchemas.JsonOptions) ?? new();
        return ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(
            api.SearchTrackerItems(input.PluginId, input.InstanceId, input.Query, input.Limit ?? 20))));
    }
    private sealed record SearchInput(string? PluginId = null, string? InstanceId = null, string? Query = null, int? Limit = null);
}

public sealed class GetTrackerItemTool(IAgentCapabilityApi api) : IAgentTool
{
    private static readonly JsonElement Schema = AgentCapabilityToolSchemas.Parse("""
        {"type":"object","properties":{"pluginId":{"type":"string"},"instanceId":{"type":"string"},"itemId":{"type":"string"}},"required":["pluginId","instanceId","itemId"],"additionalProperties":false}
        """);
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.tracker.get", "diary_get_tracker_item", "读取 Tracker 事项",
        "按本地 Tracker 实例和事项 ID 读取缓存详情，不返回凭据。",
        Schema, AgentToolOrigin.BuiltIn, AgentToolRisk.ReadOnly, "diary.ai-agent");

    public ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        var input = arguments.Deserialize<GetInput>(AgentCapabilityToolSchemas.JsonOptions);
        if (input is null || string.IsNullOrWhiteSpace(input.PluginId) || string.IsNullOrWhiteSpace(input.InstanceId) || string.IsNullOrWhiteSpace(input.ItemId))
            return ValueTask.FromResult(AgentToolResult.Failure("invalid_arguments", "Tracker 身份和事项 ID 不能为空。"));
        var item = api.GetTrackerItem(input.PluginId, input.InstanceId, input.ItemId);
        return ValueTask.FromResult(item is null
            ? AgentToolResult.Failure("tracker_item_not_found", "本地 Tracker 事项不存在。")
            : AgentToolResult.Success(JsonSerializer.Serialize(item)));
    }
    private sealed record GetInput(string PluginId, string InstanceId, string ItemId);
}

public sealed class ListScriptsTool(IAgentCapabilityApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.scripts.list", "diary_list_scripts", "列出脚本",
        "列出已加载脚本的用途、语言、参数和状态，不返回脚本源码。",
        AgentCapabilityToolSchemas.Empty, AgentToolOrigin.BuiltIn, AgentToolRisk.ReadOnly, "diary.ai-agent");
    public ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(api.ListScripts())));
}

public sealed class GetScriptContractTool(IAgentCapabilityApi api) : IAgentTool
{
    private static readonly JsonElement Schema = AgentCapabilityToolSchemas.Parse("""
        {"type":"object","properties":{"scriptId":{"type":"string"}},"required":["scriptId"],"additionalProperties":false}
        """);
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.scripts.contract", "diary_get_script_contract", "读取脚本契约",
        "读取已加载脚本的参数 schema 与诊断状态，不返回源码。",
        Schema, AgentToolOrigin.BuiltIn, AgentToolRisk.ReadOnly, "diary.ai-agent");
    public ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        var id = arguments.GetProperty("scriptId").GetString() ?? string.Empty;
        var contract = api.GetScriptContract(id);
        return ValueTask.FromResult(contract is null
            ? AgentToolResult.Failure("script_not_found", "脚本不存在或尚未加载。")
            : AgentToolResult.Success(JsonSerializer.Serialize(contract)));
    }
}

public sealed class ListRemindersTool(IAgentCapabilityApi api) : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.reminders.list", "diary_list_reminders", "列出提醒",
        "列出 AI 助手创建的应用提醒和投递状态。",
        AgentCapabilityToolSchemas.Empty, AgentToolOrigin.BuiltIn, AgentToolRisk.ReadOnly, "diary.ai-agent", "ReminderWrite");
    public ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AgentToolResult.Success(JsonSerializer.Serialize(api.ListReminders())));
}

public sealed class ScheduleReminderTool(IAgentCapabilityApi api, IAgentConfirmationService confirmations) : IAgentTool
{
    private static readonly JsonElement Schema = AgentCapabilityToolSchemas.Parse("""
        {"type":"object","properties":{"title":{"type":"string","minLength":1,"maxLength":100},"body":{"type":"string","minLength":1,"maxLength":2000},"dueAt":{"type":"string","description":"带时区的 ISO 8601 时间"}},"required":["title","body","dueAt"],"additionalProperties":false}
        """);
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.reminders.schedule", "diary_schedule_reminder", "安排提醒",
        "逐次确认后持久化应用通知提醒；不会执行代码。",
        Schema, AgentToolOrigin.BuiltIn, AgentToolRisk.Write, "diary.ai-agent", "ReminderWrite");
    public async ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        var input = arguments.Deserialize<ReminderInput>(AgentCapabilityToolSchemas.JsonOptions);
        if (input is null || string.IsNullOrWhiteSpace(input.Title) || string.IsNullOrWhiteSpace(input.Body)
            || !DateTimeOffset.TryParse(input.DueAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dueAt))
            return AgentToolResult.Failure("invalid_arguments", "提醒标题、正文或带时区时间无效。");
        if (!await ConfirmedProgramWrite.RequestAsync(confirmations, Descriptor, arguments, context, cancellationToken))
            return AgentToolResult.Failure("user_rejected", "用户拒绝创建提醒。");
        var result = await api.ScheduleReminderAsync(input.Title, input.Body, dueAt, cancellationToken);
        return result.Succeeded
            ? new AgentToolResult(true, JsonSerializer.Serialize(result), EffectSummary: result.Summary)
            : AgentToolResult.Failure(result.ErrorCode ?? "reminder_failed", result.ErrorMessage ?? "创建提醒失败。");
    }
    private sealed record ReminderInput(string Title, string Body, string DueAt);
}

public sealed class ExecuteSavedScriptTool(IAgentCapabilityApi api, IAgentConfirmationService confirmations) : IAgentTool
{
    private static readonly JsonElement Schema = AgentCapabilityToolSchemas.Parse("""
        {"type":"object","properties":{"scriptId":{"type":"string"},"arguments":{"type":"object","additionalProperties":{"type":"string"}},"idempotencyKey":{"type":"string"}},"required":["scriptId","arguments","idempotencyKey"],"additionalProperties":false}
        """);
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.scripts.execute", "diary_execute_saved_script", "执行已保存脚本",
        "只执行脚本目录中已保存并成功加载的脚本；逐次确认，不接受源码。",
        Schema, AgentToolOrigin.BuiltIn, AgentToolRisk.Write, "diary.ai-agent", "ScriptExecute");
    public async ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        var input = arguments.Deserialize<ExecuteInput>(AgentCapabilityToolSchemas.JsonOptions);
        if (input is null || string.IsNullOrWhiteSpace(input.ScriptId) || string.IsNullOrWhiteSpace(input.IdempotencyKey))
            return AgentToolResult.Failure("invalid_arguments", "脚本 ID 和幂等键不能为空。");
        var contract = api.GetScriptContract(input.ScriptId);
        if (contract is null || !string.Equals(contract.State, "Ready", StringComparison.Ordinal))
            return AgentToolResult.Failure("script_not_ready", "脚本不存在、编译失败或尚待配置。");
        var confirmation = JsonSerializer.SerializeToElement(new { contract.Id, contract.Name, contract.Language, contract.Parameters, input.Arguments });
        if (!await ConfirmedProgramWrite.RequestAsync(confirmations, Descriptor, confirmation, context, cancellationToken))
            return AgentToolResult.Failure("user_rejected", "用户拒绝执行脚本。");
        var result = await api.ExecuteSavedScriptAsync(input.ScriptId, input.Arguments, input.IdempotencyKey, cancellationToken);
        return result.Succeeded
            ? new AgentToolResult(true, JsonSerializer.Serialize(result), EffectSummary: $"脚本 {contract.Name} 执行成功。")
            : AgentToolResult.Failure(result.ErrorCode ?? "script_execution_failed", result.ErrorMessage ?? "脚本执行失败。");
    }
    private sealed record ExecuteInput(string ScriptId, IReadOnlyDictionary<string, string> Arguments, string IdempotencyKey);
}

public sealed class PreviewTrackerTimeEntriesTool(IAgentCapabilityApi api) : IAgentTool
{
    internal static readonly JsonElement Schema = AgentCapabilityToolSchemas.Parse("""
        {"type":"object","properties":{"workItemIds":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"integer","minimum":1}}},"required":["workItemIds"],"additionalProperties":false}
        """);
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.tracker-time.preview", "diary_preview_tracker_time_entries", "预览 Tracker 工时",
        "读取本地 Tracker 绑定并生成远程提交预览，不执行提交。",
        Schema, AgentToolOrigin.BuiltIn, AgentToolRisk.ReadOnly, "diary.ai-agent", "TrackerSubmit");
    public async ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        var ids = arguments.GetProperty("workItemIds").EnumerateArray().Select(item => item.GetInt32()).ToArray();
        var result = await api.PreviewTrackerTimeEntriesAsync(ids, cancellationToken);
        return result.Succeeded
            ? AgentToolResult.Success(JsonSerializer.Serialize(result))
            : AgentToolResult.Failure(result.ErrorCode ?? "preview_failed", result.ErrorMessage ?? "Tracker 工时预览失败。");
    }
}

public sealed class SubmitTrackerTimeEntriesTool(IAgentCapabilityApi api, IAgentConfirmationService confirmations) : IAgentTool
{
    private static readonly JsonElement Schema = AgentCapabilityToolSchemas.Parse("""
        {"type":"object","properties":{"workItemIds":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"integer","minimum":1}},"idempotencyKey":{"type":"string"}},"required":["workItemIds","idempotencyKey"],"additionalProperties":false}
        """);
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.tracker-time.submit", "diary_submit_tracker_time_entries", "提交 Tracker 工时",
        "逐次确认后提交本地绑定的 Tracker 工时；遇到首个失败即停止，已成功的远程记录不会回滚。",
        Schema, AgentToolOrigin.BuiltIn, AgentToolRisk.Write, "diary.ai-agent", "TrackerSubmit");
    public async ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        var input = arguments.Deserialize<SubmitInput>(AgentCapabilityToolSchemas.JsonOptions);
        if (input is null || input.WorkItemIds.Count is 0 or > 20 || string.IsNullOrWhiteSpace(input.IdempotencyKey))
            return AgentToolResult.Failure("invalid_arguments", "事项 ID 和幂等键无效。");
        var preview = await api.PreviewTrackerTimeEntriesAsync(input.WorkItemIds, cancellationToken);
        if (!preview.Succeeded || preview.PreviewVersion is null)
            return AgentToolResult.Failure(preview.ErrorCode ?? "preview_failed", preview.ErrorMessage ?? "Tracker 工时预览失败。");
        if (!await ConfirmedProgramWrite.RequestAsync(confirmations, Descriptor, JsonSerializer.SerializeToElement(preview), context, cancellationToken))
            return AgentToolResult.Failure("user_rejected", "用户拒绝提交 Tracker 工时。");
        var result = await api.SubmitTrackerTimeEntriesAsync(input.WorkItemIds, input.IdempotencyKey, preview.PreviewVersion, cancellationToken);
        return result.Succeeded
            ? new AgentToolResult(true, JsonSerializer.Serialize(result), EffectSummary: $"已提交 {result.Results.Count} 个 Tracker 工时条目。")
            : AgentToolResult.Failure(result.ErrorCode ?? "tracker_submit_failed", result.ErrorMessage ?? "Tracker 工时提交失败。");
    }
    private sealed record SubmitInput(IReadOnlyList<int> WorkItemIds, string IdempotencyKey);
}

public sealed class BatchCreateWorkItemsTool(IWorkItemCommandApi api, IAgentConfirmationService confirmations) : IAgentTool
{
    private static readonly JsonElement Schema = AgentCapabilityToolSchemas.Parse("""
        {"type":"object","properties":{"items":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"object","properties":{"date":{"type":"string"},"title":{"type":"string"},"hours":{"type":"number","exclusiveMinimum":0,"maximum":24},"priority":{"type":"integer","minimum":0,"maximum":9},"tagIds":{"type":"array","items":{"type":"integer"}},"extraFields":{"type":"array","items":{"type":"object","properties":{"fieldId":{"type":"string"},"value":{"type":"string"}},"required":["fieldId","value"],"additionalProperties":false}},"note":{"type":["string","null"]}},"required":["date","title","hours","priority","tagIds","extraFields"],"additionalProperties":false}},"idempotencyKey":{"type":"string"}},"required":["items","idempotencyKey"],"additionalProperties":false}
        """);
    public AgentToolDescriptor Descriptor { get; } = new(
        "diary.work-items.batch-create", "diary_batch_create_work_items", "批量创建事项",
        "原子预览并创建最多 20 个事项；一次确认，任一失败整体回滚，不远程上传 Tracker。",
        Schema, AgentToolOrigin.BuiltIn, AgentToolRisk.Write, "diary.ai-agent", "WorkItemWrite");
    public async ValueTask<AgentToolResult> InvokeAsync(JsonElement arguments, AgentToolInvocationContext context, CancellationToken cancellationToken = default)
    {
        var input = arguments.Deserialize<BatchCreateInput>(AgentCapabilityToolSchemas.JsonOptions);
        if (input is null || input.Items.Count is 0 or > 20 || string.IsNullOrWhiteSpace(input.IdempotencyKey))
            return AgentToolResult.Failure("invalid_arguments", "批量创建必须包含 1 到 20 项和幂等键。");
        var command = new WorkItemBatchCreateCommand(input.Items.Select((item, index) => new WorkItemCreateCommand(
            item.Date, item.Title, item.Hours, item.Priority, item.TagIds, item.ExtraFields,
            item.Note, $"{input.IdempotencyKey}:{index + 1}")).ToArray(), input.IdempotencyKey);
        var preview = await api.PreviewBatchCreateAsync(command, cancellationToken);
        if (!preview.Succeeded || preview.Command is null || preview.PreviewVersion is null)
            return AgentToolResult.Failure(preview.ErrorCode ?? "preview_failed", preview.ErrorMessage ?? "批量创建预览失败。");
        if (!await ConfirmedProgramWrite.RequestAsync(confirmations, Descriptor, JsonSerializer.SerializeToElement(preview), context, cancellationToken))
            return AgentToolResult.Failure("user_rejected", "用户拒绝批量创建事项。");
        var result = await api.BatchCreateAsync(preview.Command, cancellationToken);
        return result.Succeeded
            ? new AgentToolResult(true, JsonSerializer.Serialize(result), EffectSummary: result.Duplicate
                ? $"幂等命中，{result.WorkItemIds.Count} 个事项未重复创建。"
                : $"已原子创建 {result.WorkItemIds.Count} 个事项。")
            : AgentToolResult.Failure(result.ErrorCode ?? "batch_create_failed", result.ErrorMessage ?? "批量创建失败。");
    }
    private sealed record BatchCreateInput(IReadOnlyList<BatchCreateItem> Items, string IdempotencyKey);
    private sealed record BatchCreateItem(
        string Date,
        string Title,
        double Hours,
        int Priority,
        IReadOnlyList<int> TagIds,
        IReadOnlyList<WorkItemExtraFieldCommand> ExtraFields,
        string? Note);
}
