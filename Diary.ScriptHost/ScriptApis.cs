using Diary.ScriptBase;

namespace Diary.ScriptHost;

public interface IDiaryApi
{
    ITemplateScriptApi Templates { get; }
    IHostCapabilitiesScriptApi Host { get; }

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

public interface ISysApi
{
    ValueTask<string?> GetClipboardTextAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> SetClipboardTextAsync(string text, CancellationToken cancellationToken = default);
    ValueTask RequestMainWindowActivationAsync(CancellationToken cancellationToken = default);
    ValueTask NotifyAsync(string title, string body, CancellationToken cancellationToken = default);
    ValueTask<bool> ConfirmAsync(string title, string body, CancellationToken cancellationToken = default);
    ValueTask<OptionDialogResult> SelectOptionAsync(OptionDialogRequest request, CancellationToken cancellationToken = default);
    ValueTask<DirectorySelection?> PickDirectoryAsync(DirectoryPickerOptions options, CancellationToken cancellationToken = default);
    ValueTask<OpenExportedFileResult> AskToOpenExportedFileAsync(string fileId, CancellationToken cancellationToken = default);
}

[Obsolete("SysApi 已弃用，请改用 ISysApi。该兼容接口将在后续迁移周期内保留。")]
public interface SysApi : ISysApi;

public sealed class DiaryApi(
    IWorkItemQueryScriptApi query,
    ILogItemScriptApi logItems,
    ITemplateLogItemScriptApi templateLogItems,
    ITemplateScriptApi? templates = null,
    IHostCapabilitiesScriptApi? host = null) : IDiaryApi
{
    public ITemplateScriptApi Templates { get; } = templates ?? EmptyTemplateScriptApi.Instance;
    public IHostCapabilitiesScriptApi Host { get; } = host ?? EmptyHostCapabilitiesScriptApi.Instance;

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

internal sealed class EmptyHostCapabilitiesScriptApi : IHostCapabilitiesScriptApi
{
    public static EmptyHostCapabilitiesScriptApi Instance { get; } = new();
    public IReadOnlyList<string> List() => [];
}

#pragma warning disable CS0618 // 实现旧接口以兼容已发布的 C# 脚本。
public sealed class SystemInteractionApi(
    IClipboardScriptApi clipboard,
    IUserInteractionScriptApi interaction,
    IFileInteractionApi? fileInteraction = null) : ISysApi, SysApi
{
    public ValueTask<string?> GetClipboardTextAsync(CancellationToken cancellationToken = default) => clipboard.GetTextAsync(cancellationToken);
    public ValueTask<bool> SetClipboardTextAsync(string text, CancellationToken cancellationToken = default) => clipboard.SetTextAsync(text, cancellationToken);
    public ValueTask RequestMainWindowActivationAsync(CancellationToken cancellationToken = default) => interaction.RequestMainWindowActivationAsync(cancellationToken);
    public ValueTask NotifyAsync(string title, string body, CancellationToken cancellationToken = default) => interaction.NotifyAsync(title, body, cancellationToken);
    public ValueTask<bool> ConfirmAsync(string title, string body, CancellationToken cancellationToken = default) => interaction.ConfirmAsync(title, body, cancellationToken);
    public ValueTask<OptionDialogResult> SelectOptionAsync(OptionDialogRequest request, CancellationToken cancellationToken = default) =>
        fileInteraction is null
            ? ValueTask.FromException<OptionDialogResult>(new InvalidOperationException("选项对话框 API 未配置。"))
            : fileInteraction is IOptionDialogApi options
                ? options.SelectOptionAsync(request, cancellationToken)
                : ValueTask.FromException<OptionDialogResult>(new InvalidOperationException("选项对话框 API 未配置。"));
    public ValueTask<DirectorySelection?> PickDirectoryAsync(DirectoryPickerOptions options, CancellationToken cancellationToken = default) =>
        fileInteraction is null
            ? ValueTask.FromException<DirectorySelection?>(new InvalidOperationException("目录选择 API 未配置。"))
            : fileInteraction.PickDirectoryAsync(options, cancellationToken);
    public ValueTask<OpenExportedFileResult> AskToOpenExportedFileAsync(string fileId, CancellationToken cancellationToken = default) =>
        fileInteraction is null
            ? ValueTask.FromException<OpenExportedFileResult>(new InvalidOperationException("导出文件 API 未配置。"))
            : fileInteraction.AskToOpenExportedFileAsync(fileId, cancellationToken);
}
#pragma warning restore CS0618
