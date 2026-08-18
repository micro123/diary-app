using System.Globalization;
using Diary.Database;

namespace Diary.Db.PostgreSQL;

public sealed partial class PgDb
{
    public override DbProviderInfo GetProviderInfo()
    {
        var version = Convert.ToString(ExecuteScalar("SHOW server_version;"), CultureInfo.InvariantCulture) ?? "unknown";
        return new DbProviderInfo(
            "PostgreSQL",
            version,
            new HashSet<DbCapability>
            {
                DbCapability.Transactions,
                DbCapability.TransactionalDdl,
                DbCapability.ForeignKeys,
                DbCapability.UniqueIndexes,
                DbCapability.ReturningClause,
            });
    }

    public override DbSchemaSnapshot InspectSchema()
    {
        var tableNames = Query(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = current_schema() AND table_type = 'BASE TABLE'
            ORDER BY table_name;
            """,
            reader => reader.GetString(0))
            .Where(tableName => CoreSchemaContract.Current.FindTable(tableName) is not null)
            .ToArray();

        var columns = Query(
            """
            SELECT c.table_name, c.column_name, c.data_type, c.is_nullable,
                   EXISTS (
                       SELECT 1
                       FROM information_schema.table_constraints tc
                       JOIN information_schema.key_column_usage kcu
                         ON kcu.constraint_name = tc.constraint_name
                        AND kcu.table_schema = tc.table_schema
                        AND kcu.table_name = tc.table_name
                       WHERE tc.table_schema = c.table_schema
                         AND tc.table_name = c.table_name
                         AND kcu.column_name = c.column_name
                         AND tc.constraint_type = 'PRIMARY KEY'
                   )
            FROM information_schema.columns c
            WHERE c.table_schema = current_schema()
            ORDER BY c.table_name, c.ordinal_position;
            """,
            reader => new PgColumn(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4)));

        var indexes = Query(
            """
            SELECT tablename, indexname, indexdef LIKE '%UNIQUE INDEX%' AS is_unique,
                   regexp_replace(
                       substring(indexdef FROM '\((.*)\)'),
                       '\s+COLLATE\s+[^, )]+', '', 'gi') AS index_columns
            FROM pg_indexes
            WHERE schemaname = current_schema()
            ORDER BY tablename, indexname;
            """,
            reader => new PgIndex(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));

        var foreignKeys = Query(
            """
            SELECT child.relname,
                   child_column.attname,
                   parent.relname,
                   parent_column.attname,
                   CASE con.confdeltype
                       WHEN 'c' THEN 'cascade'
                       WHEN 'n' THEN 'set null'
                       WHEN 'd' THEN 'set default'
                       WHEN 'r' THEN 'restrict'
                       ELSE 'no action'
                   END
            FROM pg_constraint con
            JOIN pg_class child ON child.oid = con.conrelid
            JOIN pg_namespace child_schema ON child_schema.oid = child.relnamespace
            JOIN pg_class parent ON parent.oid = con.confrelid
            JOIN LATERAL unnest(con.conkey) WITH ORDINALITY child_key(attnum, position) ON TRUE
            JOIN LATERAL unnest(con.confkey) WITH ORDINALITY parent_key(attnum, position)
              ON parent_key.position = child_key.position
            JOIN pg_attribute child_column
              ON child_column.attrelid = child.oid AND child_column.attnum = child_key.attnum
            JOIN pg_attribute parent_column
              ON parent_column.attrelid = parent.oid AND parent_column.attnum = parent_key.attnum
            WHERE con.contype = 'f'
              AND child_schema.nspname = current_schema()
            ORDER BY child.relname, child_column.attname;
            """,
            reader => new PgForeignKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));

        var tables = tableNames.Select(tableName => new DbTableSchema(
            tableName,
            columns.Where(column => string.Equals(column.Table, tableName, StringComparison.OrdinalIgnoreCase))
                .Where(column => CoreSchemaContract.Current.FindTable(tableName)!.Columns.Any(expected =>
                    string.Equals(expected.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(column => new DbColumnSchema(
                    column.Name,
                    NormalizeType(column.Name, column.Type),
                    string.Equals(column.Nullable, "YES", StringComparison.OrdinalIgnoreCase),
                    column.PrimaryKey))
                .ToArray(),
            indexes.Where(index => string.Equals(index.Table, tableName, StringComparison.OrdinalIgnoreCase))
                .Where(index => CoreSchemaContract.Current.FindTable(tableName)!.Indexes.Any(expected =>
                    string.Equals(expected.Name, index.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(index => new DbIndexSchema(
                    NormalizeIndexName(index.Name),
                    index.Unique,
                    NormalizeIndexColumns(index.Columns)))
                .ToArray(),
            foreignKeys.Where(foreignKey => string.Equals(foreignKey.Table, tableName, StringComparison.OrdinalIgnoreCase))
                .Where(foreignKey => CoreSchemaContract.Current.FindTable(tableName)!.ForeignKeys.Any(expected =>
                    string.Equals(expected.Column, foreignKey.Column, StringComparison.OrdinalIgnoreCase)))
                .Select(foreignKey => new DbForeignKeySchema(
                    foreignKey.Column,
                    foreignKey.ReferencedTable,
                    foreignKey.ReferencedColumn,
                    foreignKey.DeleteAction))
                .ToArray())).ToArray();

        return new DbSchemaSnapshot(tables);
    }

    protected override IReadOnlyList<DbCompatibilityIssue> CheckDataIntegrity()
    {
        var issues = new List<DbCompatibilityIssue>();
        var duplicate = ExecuteScalar(
            """
            SELECT COUNT(*)
            FROM (
                SELECT LOWER(field_key)
                FROM tag_extra_field_definitions
                GROUP BY LOWER(field_key)
                HAVING COUNT(*) > 1
            ) duplicates;
            """);
        if (Convert.ToInt64(duplicate, CultureInfo.InvariantCulture) > 0)
        {
            issues.Add(new DbCompatibilityIssue(
                "DB-DATA-DUPLICATE-FIELD-KEY",
                DbIssueSeverity.Blocking,
                "PostgreSQL 检测到不区分大小写的重复 field_key。",
                "tag_extra_field_definitions.field_key",
                "先合并或重命名重复字段，再执行迁移。"));
        }

        return issues;
    }

    protected override DbSchemaMetadata? ReadSchemaMetadata()
    {
        return QueryFirst(
            """
            SELECT schema_version, provider_id, schema_fingerprint, migration_state,
                   last_migration_id, last_error, updated_at
            FROM diary_schema_metadata
            WHERE id = 1;
            """,
            reader => new DbSchemaMetadata(
                Convert.ToUInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetString(2),
                ParseMigrationState(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
    }

    protected override bool WriteSchemaMetadata(DbSchemaMetadata metadata)
    {
        Execute(
            """
            INSERT INTO diary_schema_metadata
                (id, schema_version, provider_id, schema_fingerprint, migration_state,
                 last_migration_id, last_error, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (id) DO UPDATE SET
                schema_version = EXCLUDED.schema_version,
                provider_id = EXCLUDED.provider_id,
                schema_fingerprint = EXCLUDED.schema_fingerprint,
                migration_state = EXCLUDED.migration_state,
                last_migration_id = EXCLUDED.last_migration_id,
                last_error = EXCLUDED.last_error,
                updated_at = EXCLUDED.updated_at;
            """,
            ("$1", 1),
            ("$2", checked((int)metadata.SchemaVersion)),
            ("$3", metadata.ProviderId),
            ("$4", metadata.SchemaFingerprint),
            ("$5", metadata.MigrationState.ToString()),
            ("$6", metadata.LastMigrationId),
            ("$7", metadata.LastError),
            ("$8", metadata.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)));
        return true;
    }

    protected override bool RecordMigrationHistory(DbMigrationHistoryEntry entry)
    {
        Execute(
            """
            INSERT INTO diary_schema_migrations
                (migration_id, version_from, version_to, checksum, applied_at, success, error)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (migration_id) DO UPDATE SET
                version_from = EXCLUDED.version_from,
                version_to = EXCLUDED.version_to,
                checksum = EXCLUDED.checksum,
                applied_at = EXCLUDED.applied_at,
                success = EXCLUDED.success,
                error = EXCLUDED.error;
            """,
            ("$1", entry.MigrationId),
            ("$2", checked((int)entry.VersionFrom)),
            ("$3", checked((int)entry.VersionTo)),
            ("$4", entry.Checksum),
            ("$5", entry.AppliedAt),
            ("$6", entry.Success),
            ("$7", entry.Error));
        return true;
    }

    private sealed record PgColumn(string Table, string Name, string Type, string Nullable, bool PrimaryKey);
    private sealed record PgIndex(string Table, string Name, bool Unique, string Columns);
    private sealed record PgForeignKey(
        string Table,
        string Column,
        string ReferencedTable,
        string ReferencedColumn,
        string DeleteAction);

    private static string NormalizeType(string columnName, string type)
    {
        if (columnName is "is_disabled" or "is_read_only" or "enabled" or "success")
            return "boolean";
        return type.ToLowerInvariant() switch
        {
            "integer" or "bigint" or "smallint" => "integer",
            "real" or "double precision" or "numeric" => "real",
            "boolean" => "boolean",
            "character" or "character varying" or "text" or "uuid" => "string",
            _ => type.ToLowerInvariant(),
        };
    }

    private static string NormalizeIndexName(string name) => name;

    private static IReadOnlyList<string> NormalizeIndexColumns(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(column => column
                .Replace("lower(", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(")", string.Empty, StringComparison.Ordinal)
                .Trim('"', ' '))
            .ToArray();
    }

    private static DbMigrationState ParseMigrationState(string value)
        => Enum.TryParse<DbMigrationState>(value, true, out var state) ? state : DbMigrationState.Failed;
}
