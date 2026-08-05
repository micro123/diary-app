using Diary.Database;
using Diary.PluginBase;
using Microsoft.Extensions.Logging;

namespace Diary.App;

public sealed class TrackerInstanceCoordinator(
    PluginInstanceRegistry registry,
    ILogger<TrackerInstanceCoordinator> logger)
{
    public void Register(
        ITrackerPlugin plugin,
        IEnumerable<PluginInstanceRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            if (registry.GetEntry(plugin.Manifest.Id, registration.InstanceId) is not null)
                continue;

            if (registration.State == TrackerInstanceState.Enabled)
            {
                var result = registry.Create(plugin, registration.InstanceId, registration.Configuration!);
                if (result.Success)
                {
                    logger.LogInformation(
                        "Tracker instance {PluginId}/{InstanceId} enabled",
                        plugin.Manifest.Id, registration.InstanceId);
                }
                else
                {
                    logger.LogError(
                        "Tracker instance {PluginId}/{InstanceId} blocked: {Error}",
                        plugin.Manifest.Id, registration.InstanceId, result.Error);
                }
            }
            else
            {
                registry.Record(plugin.Manifest.Id, registration.InstanceId, registration.State, registration.Error);
                logger.LogWarning(
                    "Tracker instance {PluginId}/{InstanceId} {State}: {Error}",
                    plugin.Manifest.Id, registration.InstanceId, registration.State, registration.Error);
            }
        }
    }

    /// <summary>
    /// 重试某个迁移失败/扩展缺失的实例：清除注册表失败条目并失效扩展缓存后，
    /// 重新让插件生成该实例的注册项并走 <see cref="Register"/> 路由。
    /// 传入重建的 <paramref name="hostContext"/>（数据库 + 配置）。
    /// </summary>
    public void Retry(ITrackerPlugin plugin, string instanceId, PluginHostContext hostContext)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(hostContext);

        registry.Clear(plugin.Manifest.Id, instanceId);
        if (hostContext.Database is DbInterfaceBase db)
            db.InvalidateExtensions(instanceId);

        logger.LogInformation(
            "Retrying tracker instance {PluginId}/{InstanceId}",
            plugin.Manifest.Id, instanceId);

        var registrations = plugin.GetInstanceRegistrations(hostContext)
            .Where(r => r.InstanceId == instanceId);
        Register(plugin, registrations);
    }
}
