using Diary.Database;
using Diary.PluginBase;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.Jira;

public sealed class JiraPlugin : ITrackerPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = JiraPluginConstants.PluginId,
        Version = "1.0.0",
        ApiVersion = 1,
        SupportsMultipleInstances = true,
        MinCoreDataVersion = 0,
        RequiredCapabilities = new[]
        {
            PluginCapabilities.ForeignKeys,
            PluginCapabilities.MultipleStatementExecution,
        },
    };

    public void RegisterServices(IServiceCollection services)
        => services.AddSingleton<IJiraApi, JiraApi>();

    public object CreateConfiguration() => JiraConfigurationStore.Current;

    public IEnumerable<IPluginMigration> GetMigrations() => [new JiraInitialMigration()];

    public IEnumerable<IPluginConfigurationMigration> GetConfigurationMigrations()
        => [new JiraConfigurationMigration()];

    public IEnumerable<PluginInstanceConfiguration> GetInstanceConfigurations(object configuration)
        => configuration is JiraPluginConfig jira
            ? jira.Instances.Select(settings => new PluginInstanceConfiguration(
                settings.InstanceId, settings, settings.Enabled, settings.DisplayName))
            : Array.Empty<PluginInstanceConfiguration>();

    public bool TrySetInstanceEnabled(object configuration, string instanceId, bool enabled)
    {
        if (configuration is not JiraPluginConfig jira)
            return false;
        var settings = jira.Instances.FirstOrDefault(item => item.InstanceId == instanceId);
        if (settings is null)
            return false;
        settings.Enabled = enabled;
        return true;
    }

    public ITrackerInstance CreateInstance(string instanceId, object configuration)
        => configuration is JiraInstanceConfiguration instanceConfiguration
            && instanceConfiguration.InstanceId == instanceId
                ? new JiraInstance(instanceConfiguration)
                : throw new ArgumentException("Jira 实例配置类型或实例 ID 无效", nameof(configuration));

    public IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext)
    {
        if (hostContext is not PluginHostContext context
            || context.Database is not DbInterfaceBase db
            || context.Configuration is not JiraPluginConfig configuration)
            return Array.Empty<PluginInstanceRegistration>();

        var registrations = new List<PluginInstanceRegistration>();
        var configuredInstances = context.InstanceConfigurations.Count > 0
            ? context.InstanceConfigurations
            : configuration.Instances.Select(settings => new PluginInstanceConfiguration(
                settings.InstanceId, settings, settings.Enabled, settings.DisplayName)).ToArray();
        foreach (var instanceConfiguration in configuredInstances.Where(item => item.Enabled))
        {
            if (instanceConfiguration.Configuration is not JiraInstanceSettings settings)
            {
                registrations.Add(new(instanceConfiguration.InstanceId, null, TrackerInstanceState.Blocked, "Jira 实例配置类型无效"));
                continue;
            }

            IJiraDb? database;
            try
            {
                database = db.GetExtension<IJiraDb>(settings.InstanceId, GetMigrations());
            }
            catch (PluginExtensionInitException exception)
            {
                registrations.Add(new(settings.InstanceId, null, TrackerInstanceState.MigrationFailed, exception.Message));
                continue;
            }

            if (database is null)
            {
                registrations.Add(new(settings.InstanceId, null, TrackerInstanceState.NotConfigured, "Jira 数据库扩展不可用"));
                continue;
            }

            registrations.Add(new(
                settings.InstanceId,
                new JiraInstanceConfiguration(settings.InstanceId, settings.DisplayName, settings, database),
                TrackerInstanceState.Enabled));
        }

        return registrations;
    }

    public bool TryDeleteInstanceData(PluginHostContext hostContext, string instanceId)
        => hostContext.Database is DbInterfaceBase db
            && db.GetExtension<IJiraDb>(instanceId, GetMigrations())?.ClearData() == true;
}
