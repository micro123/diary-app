using Diary.Database;

namespace Diary.Db.SQLite;

internal static class DbRecords
{
    public static Migration? GetMigration(uint version) => null; // currently no data upgrades
}
