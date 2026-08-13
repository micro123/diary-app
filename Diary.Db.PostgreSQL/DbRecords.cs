using Diary.Database;

namespace Diary.Db.PostgreSQL;

public static class DbRecords
{
    public static Migration? GetMigration(uint version) => null; // currently no data upgrades
}
