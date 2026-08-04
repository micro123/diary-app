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
        => new IPluginMigration[] { new RedMineInitialMigration(), new RedMineInstanceMigration() };

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

        return configuration.Instances
            .Where(x => x.Enabled)
            .Select(settings =>
            {
                var database = db.GetExtension<IRedMineDb>(settings.InstanceId, GetMigrations());
                return database is null
                    ? null
                    : new PluginInstanceRegistration(
                        settings.InstanceId,
                        new RedMineInstanceConfiguration(
                            settings.InstanceId,
                            settings.DisplayName,
                            settings,
                            database));
            })
            .Where(x => x is not null)
            .Cast<PluginInstanceRegistration>();
    }
}
