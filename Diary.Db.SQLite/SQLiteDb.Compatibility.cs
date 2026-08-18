using System.Data.Common;
using System.Globalization;
using Diary.Database;

namespace Diary.Db.SQLite;

public sealed partial class SQLiteDb
{
    private string _sqliteVersion = "unknown";

    public override DbProviderInfo GetProviderInfo()
    {
        var capabilities = new HashSet<DbCapability>
        {
            DbCapability.Transactions,
            DbCapability.TransactionalDdl,
            DbCapability.UniqueIndexes,
        };
        if (Convert.ToInt32(ExecuteScalar("PRAGMA foreign_keys;"), CultureInfo.InvariantCulture) == 1)
            capabilities.Add(DbCapability.ForeignKeys);
        if (IsReturningSupported(_sqliteVersion))
            capabilities.Add(DbCapability.ReturningClause);
        return new DbProviderInfo("SQLite", _sqliteVersion, capabilities);
    }

    public override DbSchemaSnapshot InspectSchema()
    {
        var tables = new List<DbTableSchema>();
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var tableNames = new List<string>();
        while (reader.Read())
        {
            var tableName = reader.GetString(0);
            if (CoreSchemaContract.Current.FindTable(tableName) is not null)
                tableNames.Add(tableName);
        }

        foreach (var tableName in tableNames)
            tables.Add(ReadTableSchema(tableName));
        return new DbSchemaSnapshot(tables);
    }

    protected override IReadOnlyList<DbCompatibilityIssue> CheckDataIntegrity()
    {
        var issues = new List<DbCompatibilityIssue>();
        using (var command = _connection!.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                issues.Add(new DbCompatibilityIssue(
                    "DB-DATA-FOREIGN-KEY-VIOLATION",
                    DbIssueSeverity.Blocking,
                    "SQLite 检测到违反外键约束的历史数据。",
                    SuggestedAction: "先备份并修复孤儿记录，不要直接执行迁移。"));
            }
        }

        using (var command = _connection!.CreateCommand())
        {
            command.CommandText = """
                SELECT field_key
                FROM tag_extra_field_definitions
                GROUP BY field_key COLLATE NOCASE
                HAVING COUNT(*) > 1
                LIMIT 1;
                """;
            if (command.ExecuteScalar() is not null)
            {
                issues.Add(new DbCompatibilityIssue(
                    "DB-DATA-DUPLICATE-FIELD-KEY",
                    DbIssueSeverity.Blocking,
                    "标签附加字段存在不区分大小写的重复 field_key。",
                    "tag_extra_field_definitions.field_key",
                    "先合并或重命名重复字段，再执行迁移。"));
            }
        }

        return issues;
    }

    protected override DbSchemaMetadata? ReadSchemaMetadata()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT schema_version, provider_id, schema_fingerprint, migration_state,
                   last_migration_id, last_error, updated_at
            FROM diary_schema_metadata
            WHERE id = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new DbSchemaMetadata(
            Convert.ToUInt32(reader.GetValue(0), CultureInfo.InvariantCulture),
            reader.GetString(1),
            reader.GetString(2),
            ParseMigrationState(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture));
    }

    protected override bool WriteSchemaMetadata(DbSchemaMetadata metadata)
    {
        using var command = CreateCommand(string.Empty);
        command.CommandText = """
            INSERT INTO diary_schema_metadata
                (id, schema_version, provider_id, schema_fingerprint, migration_state,
                 last_migration_id, last_error, updated_at)
            VALUES ($id, $version, $provider, $fingerprint, $state, $migration, $error, $updated)
            ON CONFLICT(id) DO UPDATE SET
                schema_version = excluded.schema_version,
                provider_id = excluded.provider_id,
                schema_fingerprint = excluded.schema_fingerprint,
                migration_state = excluded.migration_state,
                last_migration_id = excluded.last_migration_id,
                last_error = excluded.last_error,
                updated_at = excluded.updated_at;
            """;
        Bind(command, "$id", 1);
        Bind(command, "$version", metadata.SchemaVersion);
        Bind(command, "$provider", metadata.ProviderId);
        Bind(command, "$fingerprint", metadata.SchemaFingerprint);
        Bind(command, "$state", metadata.MigrationState.ToString());
        Bind(command, "$migration", metadata.LastMigrationId);
        Bind(command, "$error", metadata.LastError);
        Bind(command, "$updated", metadata.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        return true;
    }

    protected override bool RecordMigrationHistory(DbMigrationHistoryEntry entry)
    {
        using var command = CreateCommand(string.Empty);
        command.CommandText = """
            INSERT INTO diary_schema_migrations
                (migration_id, version_from, version_to, checksum, applied_at, success, error)
            VALUES ($id, $from, $to, $checksum, $applied, $success, $error)
            ON CONFLICT(migration_id) DO UPDATE SET
                version_from = excluded.version_from,
                version_to = excluded.version_to,
                checksum = excluded.checksum,
                applied_at = excluded.applied_at,
                success = excluded.success,
                error = excluded.error;
            """;
        Bind(command, "$id", entry.MigrationId);
        Bind(command, "$from", entry.VersionFrom);
        Bind(command, "$to", entry.VersionTo);
        Bind(command, "$checksum", entry.Checksum);
        Bind(command, "$applied", entry.AppliedAt.ToString("O", CultureInfo.InvariantCulture));
        Bind(command, "$success", entry.Success ? 1 : 0);
        Bind(command, "$error", entry.Error);
        command.ExecuteNonQuery();
        return true;
    }

    private DbTableSchema ReadTableSchema(string tableName)
    {
        var columns = new List<DbColumnSchema>();
        using (var command = _connection!.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                columns.Add(new DbColumnSchema(
                    name,
                    NormalizeType(name, reader.IsDBNull(2) ? string.Empty : reader.GetString(2)),
                    reader.GetInt32(3) == 0 && reader.GetInt32(5) == 0,
                    reader.GetInt32(5) > 0));
            }
        }

        var indexes = new List<DbIndexSchema>();
        using (var command = _connection!.CreateCommand())
        {
            command.CommandText = $"PRAGMA index_list({QuoteIdentifier(tableName)});";
            using var reader = command.ExecuteReader();
            var indexNames = new List<(string Name, bool Unique)>();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                if (!name.StartsWith("sqlite_autoindex_", StringComparison.OrdinalIgnoreCase))
                    indexNames.Add((name, reader.GetInt32(2) != 0));
            }

            foreach (var (name, unique) in indexNames)
            {
                using var indexCommand = _connection!.CreateCommand();
                indexCommand.CommandText = $"PRAGMA index_info({QuoteIdentifier(name)});";
                using var indexReader = indexCommand.ExecuteReader();
                var indexColumns = new List<string>();
                while (indexReader.Read())
                    indexColumns.Add(indexReader.GetString(2));
                indexes.Add(new DbIndexSchema(name, unique, indexColumns));
            }
        }

        var foreignKeys = new List<DbForeignKeySchema>();
        using (var command = _connection!.CreateCommand())
        {
            command.CommandText = $"PRAGMA foreign_key_list({QuoteIdentifier(tableName)});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                foreignKeys.Add(new DbForeignKeySchema(
                    reader.GetString(3),
                    reader.GetString(2),
                    reader.GetString(4),
                    reader.GetString(6).ToLowerInvariant()));
            }
        }

        var contract = CoreSchemaContract.Current.FindTable(tableName)!;
        return new DbTableSchema(
            tableName,
            columns.Where(column => contract.Columns.Any(expected =>
                    string.Equals(expected.Name, column.Name, StringComparison.OrdinalIgnoreCase)))
                .ToArray(),
            indexes.Where(index => contract.Indexes.Any(expected =>
                    string.Equals(expected.Name, index.Name, StringComparison.OrdinalIgnoreCase)))
                .ToArray(),
            foreignKeys.Where(foreignKey => contract.ForeignKeys.Any(expected =>
                    string.Equals(expected.Column, foreignKey.Column, StringComparison.OrdinalIgnoreCase)))
                .ToArray());
    }

    private static void Bind(DbCommand command, string name, object? value)
        => ((System.Data.SQLite.SQLiteCommand)command).Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string NormalizeType(string columnName, string type)
    {
        if (columnName is "is_disabled" or "is_read_only" or "enabled" or "success")
            return "boolean";
        var upper = type.ToUpperInvariant();
        if (upper.Contains("INT", StringComparison.Ordinal))
            return "integer";
        if (upper.Contains("CHAR", StringComparison.Ordinal) ||
            upper.Contains("TEXT", StringComparison.Ordinal) ||
            upper.Contains("CLOB", StringComparison.Ordinal))
            return "string";
        if (upper.Contains("REAL", StringComparison.Ordinal) ||
            upper.Contains("FLOA", StringComparison.Ordinal) ||
            upper.Contains("DOUB", StringComparison.Ordinal))
            return "real";
        if (upper.Contains("BOOL", StringComparison.Ordinal))
            return "boolean";
        return upper.ToLowerInvariant();
    }

    private static DbMigrationState ParseMigrationState(string value)
        => Enum.TryParse<DbMigrationState>(value, true, out var state) ? state : DbMigrationState.Failed;

    private static bool IsReturningSupported(string version)
        => Version.TryParse(version, out var parsed) && parsed >= new Version(3, 35);
}
