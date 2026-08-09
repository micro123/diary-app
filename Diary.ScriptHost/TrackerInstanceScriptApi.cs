using Diary.PluginBase;

namespace Diary.ScriptHost;

public enum TrackerScriptErrorCode
{
    InvalidInput = 1,
    InstanceUnavailable = 2,
}

public sealed record ScriptTrackerInstance(
    string PluginId,
    string InstanceId,
    string DisplayName,
    string Icon,
    bool IsConfigured);

public sealed record TrackerScriptResult(
    bool Succeeded,
    ScriptTrackerInstance? Instance,
    TrackerScriptErrorCode? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static TrackerScriptResult Success(ScriptTrackerInstance instance) => new(true, instance);
    public static TrackerScriptResult Failure(TrackerScriptErrorCode code, string message) =>
        new(false, null, code, message);
}

public interface ITrackerInstanceScriptApi
{
    TrackerScriptResult Get(string pluginId, string instanceId);
    IReadOnlyList<ScriptTrackerInstance> List();
}

public sealed class TrackerInstanceScriptApi(PluginInstanceRegistry registry) : ITrackerInstanceScriptApi
{
    public TrackerScriptResult Get(string pluginId, string instanceId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(instanceId))
            return TrackerScriptResult.Failure(
                TrackerScriptErrorCode.InvalidInput,
                "PluginId 和 InstanceId 不能为空。");
        var instance = registry.Get(pluginId, instanceId);
        return instance is null
            ? TrackerScriptResult.Failure(
                TrackerScriptErrorCode.InstanceUnavailable,
                "指定的 Tracker 实例不存在或未启用。")
            : TrackerScriptResult.Success(ToScriptInstance(instance));
    }

    public IReadOnlyList<ScriptTrackerInstance> List() =>
        registry.AllEntriesWithIdentity
            .Where(item => item.Entry.State == TrackerInstanceState.Enabled && item.Entry.Instance is not null)
            .Select(item => ToScriptInstance(item.Entry.Instance!))
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PluginId, StringComparer.Ordinal)
            .ThenBy(item => item.InstanceId, StringComparer.Ordinal)
            .ToArray();

    private static ScriptTrackerInstance ToScriptInstance(ITrackerInstance instance) => new(
        instance.PluginId,
        instance.InstanceId,
        instance.DisplayName,
        instance.Icon,
        instance.IsConfigured);
}
