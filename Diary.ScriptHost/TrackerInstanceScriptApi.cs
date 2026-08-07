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
            : TrackerScriptResult.Success(new ScriptTrackerInstance(
                instance.PluginId,
                instance.InstanceId,
                instance.DisplayName,
                instance.Icon,
                instance.IsConfigured));
    }
}
