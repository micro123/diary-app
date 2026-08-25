using Diary.Database;

namespace Diary.Db.SQLite;

internal static class DbRecords
{
    public static Migration? GetMigration(uint version) => version switch
    {
        0x00010000 => new SQLiteMigration(
            0x00010000,
            0x00010001,
            "ALTER TABLE tag_extra_field_definitions ADD COLUMN default_value TEXT NOT NULL DEFAULT ''",
            "INSERT OR IGNORE INTO data_versions(version_code) VALUES(65537)"),
        _ => null,
    };
}
