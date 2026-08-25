namespace Diary.ScriptHost;

public sealed class WorkerTrackerApiProxy(ITrackerInstanceScriptApi instances) : ITrackerApi
{
    public TrackerScriptResult GetInstance(string pluginId, string instanceId) => instances.Get(pluginId, instanceId);
    public IReadOnlyList<ScriptTrackerInstance> ListInstances() => instances.List();
}

#pragma warning disable CS0618 // 实现旧接口以兼容已发布的 C# 脚本。
public sealed class WorkerSystemInteractionApiProxy(
    IClipboardScriptApi clipboard,
    IUserInteractionScriptApi interaction,
    IFileInteractionApi fileInteraction) : ISysApi, SysApi
{
    public ValueTask<string?> GetClipboardTextAsync(CancellationToken cancellationToken = default) => clipboard.GetTextAsync(cancellationToken);
    public ValueTask<bool> SetClipboardTextAsync(string text, CancellationToken cancellationToken = default) => clipboard.SetTextAsync(text, cancellationToken);
    public ValueTask RequestMainWindowActivationAsync(CancellationToken cancellationToken = default) => interaction.RequestMainWindowActivationAsync(cancellationToken);
    public ValueTask NotifyAsync(string title, string body, CancellationToken cancellationToken = default) => interaction.NotifyAsync(title, body, cancellationToken);
    public ValueTask<bool> ConfirmAsync(string title, string body, CancellationToken cancellationToken = default) => interaction.ConfirmAsync(title, body, cancellationToken);
    public ValueTask<OptionDialogResult> SelectOptionAsync(OptionDialogRequest request, CancellationToken cancellationToken = default) => fileInteraction.SelectOptionAsync(request, cancellationToken);
    public ValueTask<DirectorySelection?> PickDirectoryAsync(DirectoryPickerOptions options, CancellationToken cancellationToken = default) => fileInteraction.PickDirectoryAsync(options, cancellationToken);
    public ValueTask<OpenExportedFileResult> AskToOpenExportedFileAsync(string fileId, CancellationToken cancellationToken = default) => fileInteraction.AskToOpenExportedFileAsync(fileId, cancellationToken);
}
#pragma warning restore CS0618
