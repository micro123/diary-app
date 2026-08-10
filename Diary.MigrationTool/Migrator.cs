using Diary.Database;
using Diary.MigrationTool.Impl;

namespace Diary.MigrationTool;

/// <summary>
/// 这个工具类将旧的日志工具 DiaryToolpp 的数据库迁移到当前数据库中。
/// </summary>
public static class Migrator
{
    public static bool MigrateFromSqlite(DbInterfaceBase db, string oldDatabase, Action<bool, double, string> processCallback)
        => RunMigration(
            db,
            processCallback,
            () =>
            {
                using var migrator = new SqliteMigrator(db, oldDatabase, processCallback);
                return migrator.DoMigrate();
            });

    public static bool MigrateFromPgsql(DbInterfaceBase db, string host, ushort port, string database, string user, string password, Action<bool, double, string> processCallback)
        => RunMigration(
            db,
            processCallback,
            () =>
            {
                using var migrator = PgMigrator.Create(db, host, port, database, user, password, processCallback);
                return migrator.DoMigrate();
            });

    private static bool RunMigration(
        DbInterfaceBase db,
        Action<bool, double, string> processCallback,
        Func<bool> migration)
    {
        var transactionStarted = false;
        var committed = false;
        try
        {
            if (!db.BeginTransaction())
            {
                processCallback(false, 1.0, "无法开始迁移事务");
                return false;
            }

            transactionStarted = true;
            if (!migration())
                return false;

            var result = db.CommitTransaction();
            committed = result;
            transactionStarted = false;
            return result;
        }
        catch (Exception ex)
        {
            processCallback(false, 1.0, $"迁移异常：{ex.Message}");
            return false;
        }
        finally
        {
            if (transactionStarted && !committed)
            {
                try
                {
                    db.RollbackTransaction();
                }
                catch (Exception)
                {
                    // 保留原始迁移失败结果，回滚异常不能覆盖它。
                }
            }
        }
    }
}

