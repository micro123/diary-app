using Diary.Database;

namespace Diary.Db.PostgreSQL;

public static class DbRecords
{
    public static Migration? GetMigration(uint version) => version switch
    {
        0x00010000 => new PgMigration(
            0x00010000,
            0x00010001,
            "ALTER TABLE tag_extra_field_definitions ADD COLUMN default_value TEXT NOT NULL DEFAULT ''",
            "INSERT INTO data_versions(version_code) VALUES(65537) ON CONFLICT DO NOTHING"),
        _ => null,
    };
}
