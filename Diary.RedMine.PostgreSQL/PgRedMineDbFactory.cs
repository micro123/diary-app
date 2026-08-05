using Diary.Database;
using Diary.PluginBase;
using Diary.RedMine;

namespace Diary.Db.PostgreSQL;

public sealed class PgRedMineDbFactory : IDbExtensionFactory
{
    public bool Supports(Type extensionType, string providerName)
        => extensionType == typeof(IRedMineDb) && providerName == "PgDb";

    public object? Create(
        IDbExtensionHost host,
        string instanceId,
        IReadOnlyList<IPluginMigration> migrations)
    {
        var extension = new PgRedMineDb(host, instanceId);
        if (!extension.Initialize(migrations, out var error))
            throw new PluginExtensionInitException(error ?? "RedMine 数据库初始化或迁移失败");
        return extension;
    }
}
