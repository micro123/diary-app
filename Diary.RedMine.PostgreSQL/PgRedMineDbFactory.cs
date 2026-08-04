using Diary.Database;
using Diary.RedMine;

namespace Diary.Db.PostgreSQL;

public sealed class PgRedMineDbFactory : IDbExtensionFactory
{
    public bool Supports(Type extensionType, string providerName)
        => extensionType == typeof(IRedMineDb) && providerName == "PgDb";

    public object? Create(IDbExtensionHost host)
    {
        var extension = new PgRedMineDb(host);
        return extension.Initialize() ? extension : null;
    }
}
