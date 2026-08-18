namespace Diary.Database;

public static class CoreSchemaContract
{
    public static DbSchemaSnapshot Current { get; } = new(
    [
        Table("work_tags",
        [
            Column("id", "integer", false, true),
            Column("tag_name", "string", false),
            Column("tag_color", "integer", false),
            Column("tag_level", "integer", false),
            Column("is_disabled", "boolean", false),
            Column("tag_metadata", "string", false),
        ],
        [
            Index("ux_work_tags_name", true, "tag_name"),
        ]),
        Table("work_items",
        [
            Column("id", "integer", false, true),
            Column("create_date", "string", false),
            Column("comment", "string", false),
            Column("hours", "real", true),
            Column("priority", "integer", true),
            Column("is_read_only", "boolean", false),
        ],
        [
            Index("idx_work_items_date", false, "create_date"),
        ]),
        Table("work_notes",
        [
            Column("id", "integer", false, true),
            Column("note", "string", false),
        ], foreignKeys:
        [
            ForeignKey("id", "work_items", "id", "cascade"),
        ]),
        Table("work_item_tags",
        [
            Column("work_id", "integer", false, true),
            Column("tag_id", "integer", false, true),
        ],
        [
            Index("idx_work_item_tags_tag", false, "tag_id"),
            Index("idx_work_item_tags_work", false, "work_id"),
        ],
        foreignKeys:
        [
            ForeignKey("work_id", "work_items", "id", "cascade"),
            ForeignKey("tag_id", "work_tags", "id", "cascade"),
        ]),
        Table("tag_extra_field_definitions",
        [
            Column("field_id", "string", false, true),
            Column("field_key", "string", false),
            Column("tag_id", "integer", false),
            Column("label", "string", false),
            Column("field_type", "integer", false),
            Column("description", "string", false),
            Column("sort_order", "integer", false),
            Column("options_json", "string", false),
            Column("enabled", "boolean", false),
        ],
        [
            Index("ux_tag_extra_fields_key", true, "field_key"),
            Index("idx_tag_extra_fields_tag", false, "tag_id", "enabled", "sort_order"),
        ],
        [
            ForeignKey("tag_id", "work_tags", "id", "cascade"),
        ]),
        Table("work_item_extra_field_values",
        [
            Column("work_id", "integer", false, true),
            Column("field_id", "string", false, true),
            Column("value_json", "string", false),
        ],
        [
            Index("idx_work_item_extra_fields_work", false, "work_id"),
        ],
        [
            ForeignKey("work_id", "work_items", "id", "cascade"),
            ForeignKey("field_id", "tag_extra_field_definitions", "field_id", "no action"),
        ]),
        Table("data_versions",
        [
            Column("version_code", "integer", false, true),
        ]),
        Table("diary_schema_metadata",
        [
            Column("id", "integer", false, true),
            Column("schema_version", "integer", false),
            Column("provider_id", "string", false),
            Column("schema_fingerprint", "string", false),
            Column("migration_state", "string", false),
            Column("last_migration_id", "string", true),
            Column("last_error", "string", true),
            Column("updated_at", "string", false),
        ]),
        Table("diary_schema_migrations",
        [
            Column("migration_id", "string", false, true),
            Column("version_from", "integer", false),
            Column("version_to", "integer", false),
            Column("checksum", "string", false),
            Column("applied_at", "string", false),
            Column("success", "boolean", false),
            Column("error", "string", true),
        ]),
    ]);

    private static DbColumnSchema Column(string name, string logicalType, bool nullable, bool primaryKey = false)
        => new(name, logicalType, nullable, primaryKey);

    private static DbIndexSchema Index(string name, bool unique, params string[] columns)
        => new(name, unique, columns);

    private static DbForeignKeySchema ForeignKey(
        string column,
        string referencedTable,
        string referencedColumn,
        string deleteAction)
        => new(column, referencedTable, referencedColumn, deleteAction);

    private static DbTableSchema Table(
        string name,
        IReadOnlyList<DbColumnSchema> columns,
        IReadOnlyList<DbIndexSchema>? indexes = null,
        IReadOnlyList<DbForeignKeySchema>? foreignKeys = null)
        => new(name, columns, indexes ?? [], foreignKeys ?? []);
}
