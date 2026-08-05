using Diary.PluginBase;
using Microsoft.Extensions.Logging;

namespace Diary.App;

/// <summary>宿主已发现插件的整体加载结果，供诊断服务展示。</summary>
public sealed record TrackerPluginLoadDiagnostic(
    ITrackerPlugin Plugin,
    PluginLoadResult Result);

/// <summary>插件及其实例的通用诊断快照，不包含任何具体 tracker 类型。</summary>
public sealed record TrackerPluginDiagnosticEntry(
    string PluginId,
    string PluginVersion,
    PluginState PluginState,
    string? PluginError,
    string? InstanceId,
    string? DisplayName,
    TrackerInstanceState? InstanceState,
    string? Error,
    bool CanRetry,
    bool CanToggle);

/// <summary>
/// 汇总插件/实例状态并提供迁移失败实例重试入口。
/// </summary>
public sealed class TrackerPluginDiagnosticsService(
    PluginInstanceRegistry instanceRegistry,
    TrackerPluginLifecycleCoordinator lifecycleCoordinator,
    ILogger<TrackerPluginDiagnosticsService> logger)
{
    private IReadOnlyList<TrackerPluginLoadDiagnostic> _pluginStates
        = Array.Empty<TrackerPluginLoadDiagnostic>();

    public void SetPluginStates(IEnumerable<TrackerPluginLoadDiagnostic> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        _pluginStates = states.ToArray();
    }

    public IReadOnlyList<TrackerPluginDiagnosticEntry> GetSnapshot()
    {
        var result = new List<TrackerPluginDiagnosticEntry>();
        foreach (var state in _pluginStates)
        {
            var instances = instanceRegistry.AllEntriesWithIdentity
                .Where(entry => entry.PluginId == state.Plugin.Manifest.Id)
                .ToArray();
            if (instances.Length == 0)
            {
                result.Add(new TrackerPluginDiagnosticEntry(
                    state.Plugin.Manifest.Id,
                    state.Plugin.Manifest.Version,
                    state.Result.State,
                    state.Result.Error,
                    null,
                    null,
                    null,
                    state.Result.Error,
                    false,
                    false));
                continue;
            }

            foreach (var instance in instances)
            {
                var entry = instance.Entry;
                result.Add(new TrackerPluginDiagnosticEntry(
                    state.Plugin.Manifest.Id,
                    state.Plugin.Manifest.Version,
                    state.Result.State,
                    state.Result.Error,
                    instance.InstanceId,
                    entry.Instance?.DisplayName ?? instance.InstanceId,
                    entry.State,
                    entry.Error,
                    entry.State is TrackerInstanceState.MigrationFailed
                        or TrackerInstanceState.NotConfigured
                        or TrackerInstanceState.ConnectionFailed,
                    entry.State is TrackerInstanceState.Enabled
                        or TrackerInstanceState.Disabled));
            }
        }

        return result;
    }

    public bool Retry(string pluginId, string instanceId)
    {
        try
        {
            return lifecycleCoordinator.Retry(pluginId, instanceId);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Retry diagnostic tracker instance {PluginId}/{InstanceId} failed",
                pluginId,
                instanceId);
            return false;
        }
    }

    public bool SetInstanceEnabled(string pluginId, string instanceId, bool enabled)
        => lifecycleCoordinator.SetInstanceEnabled(pluginId, instanceId, enabled);
}
