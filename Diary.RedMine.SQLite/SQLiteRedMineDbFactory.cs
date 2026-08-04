using Diary.Database;
using Diary.PluginBase;
using Diary.RedMine;

namespace Diary.Db.SQLite;

public sealed class SQLiteRedMineDbFactory : IDbExtensionFactory
{
    public bool Supports(Type extensionType, string providerName)
        => extensionType == typeof(IRedMineDb) && providerName == "SQLiteDb";

    public object? Create(
        IDbExtensionHost host,
        string instanceId,
        IReadOnlyList<IPluginMigration> migrations)
    {
        var extension = new SQLiteRedMineDb(host, instanceId);
        var effectiveMigrations = migrations.Count > 0
            ? migrations
            : new RedMinePlugin().GetMigrations().ToArray();
        return extension.Initialize(effectiveMigrations) ? extension : null;
    }
}
