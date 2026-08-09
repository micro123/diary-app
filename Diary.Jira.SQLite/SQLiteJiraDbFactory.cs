using Diary.Database;
using Diary.Jira;
using Diary.PluginBase;

namespace Diary.Db.SQLite;

public sealed class SQLiteJiraDbFactory : IDbExtensionFactory
{
    public bool Supports(Type extensionType, string providerName)
        => extensionType == typeof(IJiraDb) && providerName == "SQLiteDb";

    public object? Create(IDbExtensionHost host, string instanceId, IReadOnlyList<IPluginMigration> migrations)
    {
        var extension = new SQLiteJiraDb(host, instanceId);
        if (!extension.Initialize(migrations, out var error))
            throw new PluginExtensionInitException(error ?? "Jira 数据库初始化或迁移失败");
        return extension;
    }
}
