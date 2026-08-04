namespace Diary.PluginBase;

public static class PluginMigrationRunner
{
    public static bool Upgrade(
        string pluginId,
        uint currentVersion,
        uint targetVersion,
        IEnumerable<IPluginMigration> migrations,
        IPluginMigrationContext context)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(context);

        if (currentVersion > targetVersion)
            return false;
        if (currentVersion == targetVersion)
            return true;

        var steps = new Dictionary<uint, IPluginMigration>();
        foreach (var migration in migrations)
        {
            if (migration.PluginId != pluginId
                || migration.FromVersion >= migration.ToVersion
                || migration.ToVersion > targetVersion
                || !steps.TryAdd(migration.FromVersion, migration))
            {
                return false;
            }
        }

        while (currentVersion < targetVersion)
        {
            if (!steps.TryGetValue(currentVersion, out var migration))
                return false;

            try
            {
                if (!migration.Up(context))
                    return false;
            }
            catch
            {
                return false;
            }

            currentVersion = migration.ToVersion;
        }

        return currentVersion == targetVersion;
    }
}
