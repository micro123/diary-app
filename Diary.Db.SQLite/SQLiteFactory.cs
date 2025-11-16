using Diary.Database;

namespace Diary.Db.SQLite;

public class SQLiteFactory: IDbFactory
{
    public string Name => "SQLite";
    public IDbInterface Create()
    {
        return new SQLiteDb();
    }
}