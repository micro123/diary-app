using Diary.Database;

namespace Diary.Db.SQLite;

public class SQLiteFactory: IDbFactory
{
    public string Name => "SQLite";
    public DbInterfaceBase Create()
    {
        return new SQLiteDb(this);
    }

    public Migration? GetMigration(uint version)
    {
        return DbRecords.GetMigration(version);
    }
}