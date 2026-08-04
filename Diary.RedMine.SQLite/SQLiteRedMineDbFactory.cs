using Diary.Database;
using Diary.RedMine;

namespace Diary.Db.SQLite;

public sealed class SQLiteRedMineDbFactory : IDbExtensionFactory
{
    public bool Supports(Type extensionType, string providerName)
        => extensionType == typeof(IRedMineDb) && providerName == "SQLiteDb";

    public object? Create(IDbExtensionHost host)
    {
        var extension = new SQLiteRedMineDb(host);
        return extension.Initialize() ? extension : null;
    }
}
