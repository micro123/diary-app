using Diary.Core.Utils;
using Diary.Database;
using Diary.Utils;

namespace Diary.Db.SQLite;

public sealed class SQLiteFactory: IDbFactory
{
    private readonly Config _config = new()
    {
        FilePath = Path.Combine(FsTools.GetApplicationDataDirectory(), "db.sqlite3"),
    };
    
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

    public object GetConfig()
    {
        return _config;
    }
}