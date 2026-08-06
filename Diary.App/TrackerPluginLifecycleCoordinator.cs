using Diary.PluginBase;
using Diary.PluginUI;
using Diary.Database;
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
    private readonly PluginConfigurationLoader _configurationLoader = new();
    private object? _database;
    private IReadOnlyList<ITrackerPlugin> _plugins = Array.Empty<ITrackerPlugin>();
    private IReadOnlyDictionary<string, object> _configurations
        = new Dictionary<string, object>();

    public void Register(
        object database,
        IEnumerable<ITrackerPlugin> plugins,
        IReadOnlyDictionary<string, object> configurations)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(configurations);

        _database = database;
        _plugins = plugins.ToArray();
        _configurations = configurations;
        instanceRegistry.ClearAll();

        foreach (var plugin in _plugins)
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

                var configuredInstances = plugin.GetInstanceConfigurations(configuration).ToArray();
                foreach (var instance in configuredInstances.Where(instance => !instance.Enabled))
                    instanceRegistry.Record(plugin.Manifest.Id, instance.InstanceId, TrackerInstanceState.Disabled, null);
                var instanceConfigurations = configuredInstances.Where(instance => instance.Enabled).ToArray();
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

    public void ReRegister()
    {
        if (_database is not null)
            Register(_database, _plugins, _configurations);
    }

    public bool SaveConfiguration(string pluginId)
    {
        var plugin = _plugins.FirstOrDefault(item => item.Manifest.Id == pluginId);
        return plugin is not null
            && _configurations.TryGetValue(pluginId, out var configuration)
            && _configurationLoader.Save(plugin, configuration);
    }

    public bool SetInstanceEnabled(string pluginId, string instanceId, bool enabled)
    {
        var plugin = _plugins.FirstOrDefault(item => item.Manifest.Id == pluginId);
        if (plugin is null || _database is null
            || !_configurations.TryGetValue(pluginId, out var configuration)
            || !plugin.TrySetInstanceEnabled(configuration, instanceId, enabled))
            return false;

        if (!_configurationLoader.Save(plugin, configuration))
            return false;

        if (!enabled)
        {
            var disabled = instanceRegistry.Disable(pluginId, instanceId);
            RefreshContributions();
            return disabled;
        }

        instanceRegistry.Clear(pluginId, instanceId);
        var context = CreateContext(plugin, configuration);
        instanceCoordinator.Register(
            plugin,
            plugin.GetInstanceRegistrations(context)
                .Where(registration => registration.InstanceId == instanceId));
        RefreshContributions();
        return instanceRegistry.GetEntry(pluginId, instanceId)?.State == TrackerInstanceState.Enabled;
    }

    /// <summary>
    /// 卸载实例默认只禁用并保留配置和本地数据；显式要求时才删除插件数据。
    /// </summary>
    public bool UninstallInstance(string pluginId, string instanceId, bool deleteData = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var plugin = _plugins.FirstOrDefault(item => item.Manifest.Id == pluginId);
        if (plugin is null || _database is null
            || !_configurations.TryGetValue(pluginId, out var configuration))
            return false;

        var context = CreateContext(plugin, configuration);
        if (!plugin.TrySetInstanceEnabled(configuration, instanceId, false))
            return false;
        if (!_configurationLoader.Save(plugin, configuration))
            return false;

        if (deleteData && !plugin.TryDeleteInstanceData(context, instanceId))
            return false;

        if (_database is DbInterfaceBase database)
        {
            database.InvalidateExtensions(instanceId);
        }

        var disabled = instanceRegistry.Disable(pluginId, instanceId);
        RefreshContributions();
        return disabled;
    }

    /// <summary>重试实例注册，并在结束后刷新 UI/模板贡献。</summary>
    public bool Retry(string pluginId, string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var plugin = _plugins.FirstOrDefault(item => item.Manifest.Id == pluginId);
        if (plugin is null || _database is null
            || !_configurations.TryGetValue(pluginId, out var configuration))
            return false;

        try
        {
            var context = CreateContext(plugin, configuration);
            instanceCoordinator.Retry(plugin, instanceId, context);
            if (instanceRegistry.GetEntry(pluginId, instanceId) is null)
            {
                instanceRegistry.Record(
                    pluginId,
                    instanceId,
                    TrackerInstanceState.MigrationFailed,
                    "插件未返回该实例的注册项");
            }
        }
        catch (Exception ex)
        {
            instanceRegistry.Clear(pluginId, instanceId);
            instanceRegistry.Record(
                pluginId,
                instanceId,
                TrackerInstanceState.MigrationFailed,
                ex.Message);
            logger.LogError(
                ex,
                "Retry tracker instance {PluginId}/{InstanceId} failed",
                pluginId,
                instanceId);
        }

        RefreshContributions();
        return instanceRegistry.GetEntry(pluginId, instanceId)?.State
            == TrackerInstanceState.Enabled;
    }

    private PluginHostContext CreateContext(ITrackerPlugin plugin, object configuration)
        => new(_database!, configuration)
        {
            InstanceConfigurations = plugin
                .GetInstanceConfigurations(configuration)
                .Where(instance => instance.Enabled)
                .ToArray(),
        };

    private void RefreshContributions()
    {
        uiRegistry.Register(uiFactories, instanceRegistry.Instances);
        templateRegistry.Register(templateFactories, instanceRegistry.Instances);
    }
}
