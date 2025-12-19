using Diary.Database;
using Diary.MigrationTool.Impl;

namespace Diary.MigrationTool;

/// <summary>
/// 这个工具类将旧的日志工具 DiaryToolpp 的数据库迁移到当前数据库中。
/// </summary>
public static class Migrator
{
    public static bool MigrateFromSqlite(DbInterfaceBase db, string oldDatabase, Action<bool, double, string> processCallback)
    {
        bool endTransaction = false;
        try
        {
            using var migrator = new SqliteMigrator(db, oldDatabase, processCallback);
            if (db.BeginTransaction())
            {
                endTransaction = true;
                bool ok = migrator.DoMigrate();
                if (ok)
                {
                    endTransaction = false;
                    return db.CommitTransaction();
                }
            }
        }
        catch (Exception)
        {
            if (endTransaction)
                db.RollbackTransaction();
        }

        return false;
    }
    
    public static bool MigrateFromPgsql(DbInterfaceBase db, string host, ushort port, string database, string user, string password, Action<bool, double, string> processCallback)
    {
        bool endTransaction = false;
        try
        {
            using var migrator = new PgMigrator(db, host, port, database, user, password, processCallback);
            if (db.BeginTransaction())
            {
                endTransaction = true;
                bool ok = migrator.DoMigrate();
                if (ok)
                {
                    endTransaction = false;
                    return db.CommitTransaction();
                }
            }
        }
        catch (Exception)
        {
            if (endTransaction)
                db.RollbackTransaction();
        }

        return false;
    }
}

