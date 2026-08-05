namespace Diary.PluginBase;

public static class PluginMigrationRunner
{
    public static bool Upgrade(
        string pluginId,
        uint currentVersion,
        uint targetVersion,
        IEnumerable<IPluginMigration> migrations,
        IPluginMigrationContext context)
        => Upgrade(pluginId, currentVersion, targetVersion, migrations, context, out _);

    /// <summary>
    /// 执行迁移链。<paramref name="error"/> 在失败时承载原因（链无效、Up 返回 false、或 Up 抛出的异常信息），
    /// 供宿主记录到 <see cref="TrackerInstanceState.MigrationFailed"/> 条目（架构 §6）。
    /// </summary>
    public static bool Upgrade(
        string pluginId,
        uint currentVersion,
        uint targetVersion,
        IEnumerable<IPluginMigration> migrations,
        IPluginMigrationContext context,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(context);

        error = null;

        if (currentVersion > targetVersion)
        {
            error = $"当前版本 {currentVersion} 高于目标版本 {targetVersion}";
            return false;
        }
        if (currentVersion == targetVersion)
            return true;

        var steps = new Dictionary<uint, IPluginMigration>();
        foreach (var migration in migrations)
        {
            if (migration.PluginId != pluginId)
            {
                error = $"迁移 PluginId {migration.PluginId} 与 {pluginId} 不匹配";
                return false;
            }
            if (migration.FromVersion >= migration.ToVersion)
            {
                error = $"迁移 {migration.FromVersion}->{migration.ToVersion} 版本倒退";
                return false;
            }
            if (migration.ToVersion > targetVersion)
            {
                error = $"迁移 {migration.FromVersion}->{migration.ToVersion} 超出目标版本 {targetVersion}";
                return false;
            }
            if (!steps.TryAdd(migration.FromVersion, migration))
            {
                error = $"重复的迁移起点 {migration.FromVersion}";
                return false;
            }
        }

        while (currentVersion < targetVersion)
        {
            if (!steps.TryGetValue(currentVersion, out var migration))
            {
                error = $"缺少 {currentVersion} 起点的迁移步骤";
                return false;
            }

            try
            {
                if (!migration.Up(context))
                {
                    error = $"迁移 {migration.FromVersion}->{migration.ToVersion} 返回失败";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = $"迁移 {migration.FromVersion}->{migration.ToVersion} 抛出异常：{ex.Message}";
                return false;
            }

            currentVersion = migration.ToVersion;
        }

        return currentVersion == targetVersion;
    }
}
