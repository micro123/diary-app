namespace Diary.ScriptHost;

public sealed class WorkerTrackerApiProxy(ITrackerInstanceScriptApi instances) : ITrackerApi
{
    public TrackerScriptResult GetInstance(string pluginId, string instanceId) => instances.Get(pluginId, instanceId);
    public IReadOnlyList<ScriptTrackerInstance> ListInstances() => instances.List();
}

public sealed class WorkerSystemInteractionApiProxy(
    IClipboardScriptApi clipboard,
    IUserInteractionScriptApi interaction) : SysApi
{
    public ValueTask<string?> GetClipboardTextAsync(CancellationToken cancellationToken = default) => clipboard.GetTextAsync(cancellationToken);
    public ValueTask<bool> SetClipboardTextAsync(string text, CancellationToken cancellationToken = default) => clipboard.SetTextAsync(text, cancellationToken);
    public ValueTask NotifyAsync(string title, string body, CancellationToken cancellationToken = default) => interaction.NotifyAsync(title, body, cancellationToken);
    public ValueTask<bool> ConfirmAsync(string title, string body, CancellationToken cancellationToken = default) => interaction.ConfirmAsync(title, body, cancellationToken);
}
