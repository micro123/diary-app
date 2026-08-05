using Diary.PluginBase;
using Diary.PluginUI;
using Microsoft.Extensions.Logging;

namespace Diary.App;

/// <summary>
/// 统一编排插件实例的配置枚举、实例创建和 UI/模板贡献注册。
/// 数据库扩展初始化仍由插件的 GetInstanceRegistrations 在插件边界内完成。
/// </summary>
public sealed class TrackerPluginLifecycleCoordinator(
    TrackerInstanceCoordinator instanceCoordinator,
    TrackerUiContributionRegistry uiRegistry,
    TrackerTemplateContributorRegistry templateRegistry,
    IEnumerable<ITrackerUiContributionFactory> uiFactories,
    IEnumerable<ITrackerTemplateContributorFactory> templateFactories,
    PluginInstanceRegistry instanceRegistry,
    ILogger<TrackerPluginLifecycleCoordinator> logger)
{
    public void Register(
        object database,
        IEnumerable<ITrackerPlugin> plugins,
        IReadOnlyDictionary<string, object> configurations)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(configurations);

        foreach (var plugin in plugins)
        {
            try
            {
                if (!configurations.TryGetValue(plugin.Manifest.Id, out var configuration))
                {
                    logger.LogWarning(
                        "Plugin {PluginId} has no loaded configuration",
                        plugin.Manifest.Id);
                    continue;
                }

                var instanceConfigurations = plugin
                    .GetInstanceConfigurations(configuration)
                    .Where(instance => instance.Enabled)
                    .ToArray();
                var context = new PluginHostContext(database, configuration)
                {
                    InstanceConfigurations = instanceConfigurations,
                };
                instanceCoordinator.Register(
                    plugin,
                    plugin.GetInstanceRegistrations(context));
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Plugin {PluginId} instance lifecycle registration failed",
                    plugin.Manifest.Id);
            }
        }

        // UI/模板只消费 Enabled 实例，因此失败或禁用条目不会被错误注册。
        uiRegistry.Register(uiFactories, instanceRegistry.Instances);
        templateRegistry.Register(templateFactories, instanceRegistry.Instances);
    }
}
