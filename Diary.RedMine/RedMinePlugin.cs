using Diary.Core.Data.AppConfig;
using Diary.PluginBase;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.RedMine;

public sealed class RedMinePlugin : ITrackerPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "tracker.redmine",
        Version = "1.0.0",
        ApiVersion = 1,
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

    public object CreateConfiguration() => new RedMineConfig();

    public IEnumerable<IPluginMigration> GetMigrations()
        => new[] { new RedMineInitialMigration() };

    public ITrackerInstance CreateInstance(string instanceId, object configuration)
    {
        if (configuration is not RedMineInstanceConfiguration instanceConfiguration
            || instanceConfiguration.InstanceId != instanceId)
        {
            throw new ArgumentException("RedMine instance configuration is invalid", nameof(configuration));
        }

        return new RedMineInstance(instanceConfiguration);
    }
}
