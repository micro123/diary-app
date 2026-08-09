using Diary.ScriptBase;

namespace Diary.ScriptHost;

public interface IDiaryApi
{
    ITemplateScriptApi Templates { get; }

    ValueTask<ScriptWorkItemQueryResult> QueryAsync(
        ScriptWorkItemQuery query,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ScriptWorkItem> StreamAsync(
        ScriptWorkItemQuery query,
        int pageSize = 500,
        CancellationToken cancellationToken = default);

    ValueTask<ScriptLogItemResult> CreateLogItemAsync(
        ScriptLogItemRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ScriptLogItemResult> CreateFromTemplateAsync(
        ScriptTemplateLogItemRequest request,
        CancellationToken cancellationToken = default);
}

public interface ITrackerApi
{
    TrackerScriptResult GetInstance(string pluginId, string instanceId);
    IReadOnlyList<ScriptTrackerInstance> ListInstances();
}

public interface SysApi
{
    ValueTask<string?> GetClipboardTextAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> SetClipboardTextAsync(string text, CancellationToken cancellationToken = default);
    ValueTask NotifyAsync(string title, string body, CancellationToken cancellationToken = default);
    ValueTask<bool> ConfirmAsync(string title, string body, CancellationToken cancellationToken = default);
}

public sealed class DiaryApi(
    IWorkItemQueryScriptApi query,
    ILogItemScriptApi logItems,
    ITemplateLogItemScriptApi templateLogItems,
    ITemplateScriptApi? templates = null) : IDiaryApi
{
    public ITemplateScriptApi Templates { get; } = templates ?? EmptyTemplateScriptApi.Instance;

    public ValueTask<ScriptWorkItemQueryResult> QueryAsync(ScriptWorkItemQuery request, CancellationToken cancellationToken = default) =>
        query.QueryAsync(request, cancellationToken);

    public IAsyncEnumerable<ScriptWorkItem> StreamAsync(ScriptWorkItemQuery request, int pageSize = 500, CancellationToken cancellationToken = default) =>
        query.StreamAsync(request, pageSize, cancellationToken);

    public ValueTask<ScriptLogItemResult> CreateLogItemAsync(ScriptLogItemRequest request, CancellationToken cancellationToken = default) =>
        logItems.CreateAsync(request, cancellationToken);

    public ValueTask<ScriptLogItemResult> CreateFromTemplateAsync(ScriptTemplateLogItemRequest request, CancellationToken cancellationToken = default) =>
        templateLogItems.CreateAsync(request, cancellationToken);
}

public sealed class TrackerApi(ITrackerInstanceScriptApi instances) : ITrackerApi
{
    public TrackerScriptResult GetInstance(string pluginId, string instanceId) => instances.Get(pluginId, instanceId);
    public IReadOnlyList<ScriptTrackerInstance> ListInstances() => instances.List();
}

internal sealed class EmptyTemplateScriptApi : ITemplateScriptApi
{
    public static EmptyTemplateScriptApi Instance { get; } = new();
    public IReadOnlyList<ScriptTemplateInfo> List() => [];
}

public sealed class SystemInteractionApi(
    IClipboardScriptApi clipboard,
    IUserInteractionScriptApi interaction) : SysApi
{
    public ValueTask<string?> GetClipboardTextAsync(CancellationToken cancellationToken = default) => clipboard.GetTextAsync(cancellationToken);
    public ValueTask<bool> SetClipboardTextAsync(string text, CancellationToken cancellationToken = default) => clipboard.SetTextAsync(text, cancellationToken);
    public ValueTask NotifyAsync(string title, string body, CancellationToken cancellationToken = default) => interaction.NotifyAsync(title, body, cancellationToken);
    public ValueTask<bool> ConfirmAsync(string title, string body, CancellationToken cancellationToken = default) => interaction.ConfirmAsync(title, body, cancellationToken);
}
