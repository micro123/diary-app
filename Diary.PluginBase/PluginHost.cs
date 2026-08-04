using Microsoft.Extensions.DependencyInjection;

namespace Diary.PluginBase;

public sealed record PluginLoadResult(PluginState State, string? Error = null);

public static class PluginHost
{
    public static PluginLoadResult Register(
        ITrackerPlugin plugin,
        PluginCompatibilityContext compatibility,
        IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(services);

        if (!PluginCompatibilityValidator.Validate(plugin.Manifest, compatibility, out var error))
            return new PluginLoadResult(PluginState.Blocked, error);

        try
        {
            plugin.RegisterServices(services);
            return new PluginLoadResult(PluginState.Compatible);
        }
        catch (Exception ex)
        {
            return new PluginLoadResult(PluginState.Blocked, ex.Message);
        }
    }

    public static PluginLoadResult Migrate(
        ITrackerPlugin plugin,
        uint currentVersion,
        IPluginMigrationContext context)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);

        var migrations = plugin.GetMigrations().ToArray();
        if (migrations.Length == 0)
            return new PluginLoadResult(PluginState.Enabled);

        var targetVersion = migrations.Max(migration => migration.ToVersion);
        var migrated = PluginMigrationRunner.Upgrade(
            plugin.Manifest.Id, currentVersion, targetVersion, migrations, context);
        return migrated
            ? new PluginLoadResult(PluginState.Enabled)
            : new PluginLoadResult(PluginState.MigrationFailed, "插件数据库迁移失败");
    }
}
