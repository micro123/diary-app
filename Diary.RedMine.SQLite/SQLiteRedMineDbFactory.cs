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
        if (!extension.Initialize(migrations, out var error))
            throw new PluginExtensionInitException(error ?? "RedMine 数据库初始化或迁移失败");
        return extension;
    }
}
