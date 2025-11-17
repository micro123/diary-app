using Diary.Database;

namespace Diary.Db.SQLite;

internal static class DbRecords
{
    public static Migration? GetMigration(uint version)
    {
        return null; // currently no data upgrades
    }
}