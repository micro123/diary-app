using Diary.Database;

namespace Diary.Db.PostgreSQL;

public static class DbRecords
{
    public static Migration? GetMigration(uint version)
    {
        return null; // currently no data upgrades
    }
}