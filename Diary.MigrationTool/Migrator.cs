using Diary.Database;

namespace Diary.MigrationTool;

/// <summary>
/// 这个工具类将旧的日志工具 DiaryToolpp 的数据库迁移到当前数据库中。
/// </summary>
public static class Migrator
{
    public static bool MigrateFromSqlite(DbInterfaceBase db, string oldDatabase)
    {
        throw new NotImplementedException();
    }
    
    public static bool MigrateFromPgsql(DbInterfaceBase db, string host, ushort port, string database, string user, string password)
    {
        throw new NotImplementedException();
    }
}

