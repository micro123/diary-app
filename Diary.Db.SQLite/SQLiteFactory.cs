using Diary.Core.Utils;
using Diary.Database;

namespace Diary.Db.SQLite;

public sealed class SQLiteFactory: IDbFactory
{
    public string Name => "SQLite";
    public bool Usable => true;
    public DbInterfaceBase Create()
    {
        return new SQLiteDb(this);
    }

    public Migration? GetMigration(uint version)
    {
        return DbRecords.GetMigration(version);
    }
}