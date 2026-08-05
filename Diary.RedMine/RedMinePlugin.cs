using Diary.PluginBase;
using Diary.Database;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.RedMine;

public sealed class RedMinePlugin : ITrackerPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = RedMinePluginConstants.PluginId,
        Version = "1.0.0",
        ApiVersion = 1,
        SupportsMultipleInstances = true,
        MinCoreDataVersion = 0,
        RequiredCapabilities = new[]
        {
            PluginCapabilities.SqlTransactions,
            PluginCapabilities.ForeignKeys,
            PluginCapabilities.MultipleStatementExecution,
        },
    };

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IRedMineApi, RedMineApi>();
    }

    public object CreateConfiguration() => RedMineConfigurationStore.Current;

    public IEnumerable<IPluginMigration> GetMigrations()
        => new IPluginMigration[] { new RedMineInitialMigration() };

    public IEnumerable<IPluginConfigurationMigration> GetConfigurationMigrations()
        => new[] { new RedMineConfigurationMigration() };

    public IEnumerable<PluginInstanceConfiguration> GetInstanceConfigurations(object configuration)
        => configuration is RedMinePluginConfig redmine
            ? GetConfiguredInstances(redmine).Select(settings => new PluginInstanceConfiguration(
                settings.InstanceId,
                settings,
                settings.Enabled,
                settings.DisplayName))
            : Array.Empty<PluginInstanceConfiguration>();

    private static IEnumerable<RedMineInstanceSettings> GetConfiguredInstances(RedMinePluginConfig configuration)
    {
        if (configuration.Instances.Count == 1
            && configuration.Instances[0].InstanceId == RedMinePluginConstants.DefaultInstanceId)
            Copy(configuration, configuration.Instances[0]);
        return configuration.Instances;
    }

    private static void Copy(RedMineConfig source, RedMineConfig target)
    {
        target.RedMineServerUrl = source.RedMineServerUrl;
        target.RedMineApiKey = source.RedMineApiKey;
        target.EnableProxy = source.EnableProxy;
        target.ProxyServer = source.ProxyServer;
    }

    public bool TrySetInstanceEnabled(object configuration, string instanceId, bool enabled)
    {
        if (configuration is not RedMinePluginConfig redmine)
            return false;
        var settings = redmine.Instances.FirstOrDefault(item => item.InstanceId == instanceId);
        if (settings is null)
            return false;
        settings.Enabled = enabled;
        return true;
    }

    public ITrackerInstance CreateInstance(string instanceId, object configuration)
    {
        if (configuration is not RedMineInstanceConfiguration instanceConfiguration
            || instanceConfiguration.InstanceId != instanceId)
        {
            throw new ArgumentException("RedMine instance configuration is invalid", nameof(configuration));
        }

        return new RedMineInstance(instanceConfiguration);
    }

    public IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext)
    {
        if (hostContext is not PluginHostContext context
            || context.Database is not DbInterfaceBase db
            || context.Configuration is not RedMinePluginConfig configuration)
            return Array.Empty<PluginInstanceRegistration>();

        var migrations = GetMigrations();
        var configuredInstances = context.InstanceConfigurations.Count > 0
            ? context.InstanceConfigurations
            : configuration.Instances.Select(settings => new PluginInstanceConfiguration(
                settings.InstanceId,
                settings,
                settings.Enabled,
                settings.DisplayName)).ToArray();
        var registrations = new List<PluginInstanceRegistration>();
        foreach (var instanceConfiguration in configuredInstances.Where(x => x.Enabled))
        {
            if (instanceConfiguration.Configuration is not RedMineInstanceSettings settings)
            {
                registrations.Add(new PluginInstanceRegistration(
                    instanceConfiguration.InstanceId,
                    null,
                    TrackerInstanceState.Blocked,
                    "RedMine 实例配置类型无效"));
                continue;
            }

            IRedMineDb? database;
            try
            {
                database = db.GetExtension<IRedMineDb>(settings.InstanceId, migrations);
            }
            catch (PluginExtensionInitException ex)
            {
                registrations.Add(new PluginInstanceRegistration(
                    settings.InstanceId,
                    null,
                    TrackerInstanceState.MigrationFailed,
                    ex.Message));
                continue;
            }

            if (database is null)
            {
                registrations.Add(new PluginInstanceRegistration(
                    settings.InstanceId,
                    null,
                    TrackerInstanceState.NotConfigured,
                    "数据库扩展不可用"));
                continue;
            }

            registrations.Add(new PluginInstanceRegistration(
                settings.InstanceId,
                new RedMineInstanceConfiguration(
                    settings.InstanceId,
                    settings.DisplayName,
                    settings,
                    database),
                TrackerInstanceState.Enabled));
        }

        return registrations;
    }

    public bool TryDeleteInstanceData(PluginHostContext hostContext, string instanceId)
    {
        if (hostContext.Database is not DbInterfaceBase db)
            return false;

        var database = db.GetExtension<IRedMineDb>(instanceId, GetMigrations());
        return database?.ClearData() == true;
    }
}
