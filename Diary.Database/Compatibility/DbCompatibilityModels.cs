using System.Security.Cryptography;
using System.Text;

namespace Diary.Database;

public enum DbCompatibilityState
{
    Uninitialized,
    Compatible,
    NeedsMigration,
    NewerThanApplication,
    MigrationUnavailable,
    SchemaDrift,
    MigrationIncomplete,
    DataIntegrityError,
    ProviderMismatch,
    CapabilityMissing,
    Unavailable,
}

public enum DbIssueSeverity
{
    Information,
    Warning,
    Error,
    Blocking,
}

public enum DbMigrationState
{
    Stable,
    Running,
    Failed,
}

public enum DbCapability
{
    Transactions,
    TransactionalDdl,
    ForeignKeys,
    UniqueIndexes,
    ReturningClause,
}

public sealed record DbCompatibilityIssue(
    string Code,
    DbIssueSeverity Severity,
    string Message,
    string? ObjectName = null,
    string? SuggestedAction = null);

public sealed record DbProviderInfo(
    string ProviderId,
    string ProviderVersion,
    IReadOnlySet<DbCapability> Capabilities);

public sealed record DbColumnSchema(
    string Name,
    string LogicalType,
    bool IsNullable,
    bool IsPrimaryKey = false);

public sealed record DbIndexSchema(
    string Name,
    bool IsUnique,
    IReadOnlyList<string> Columns);

public sealed record DbForeignKeySchema(
    string Column,
    string ReferencedTable,
    string ReferencedColumn,
    string DeleteAction);

public sealed record DbTableSchema(
    string Name,
    IReadOnlyList<DbColumnSchema> Columns,
    IReadOnlyList<DbIndexSchema> Indexes,
    IReadOnlyList<DbForeignKeySchema> ForeignKeys);

public sealed class DbSchemaSnapshot
{
    public DbSchemaSnapshot(IEnumerable<DbTableSchema> tables)
    {
        Tables = tables
            .OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Fingerprint = ComputeFingerprint(Tables);
    }

    public IReadOnlyList<DbTableSchema> Tables { get; }
    public string Fingerprint { get; }

    public DbTableSchema? FindTable(string name) => Tables.FirstOrDefault(
        table => string.Equals(table.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string ComputeFingerprint(IEnumerable<DbTableSchema> tables)
    {
        var canonical = new StringBuilder();
        foreach (var table in tables.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            canonical.Append("table:").Append(table.Name.ToLowerInvariant()).Append('\n');
            foreach (var column in table.Columns.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                canonical
                    .Append("column:")
                    .Append(column.Name.ToLowerInvariant()).Append('|')
                    .Append(column.LogicalType.ToLowerInvariant()).Append('|')
                    .Append(column.IsNullable ? "nullable" : "required").Append('|')
                    .Append(column.IsPrimaryKey ? "pk" : "non-pk")
                    .Append('\n');
            }

            foreach (var index in table.Indexes.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                canonical
                    .Append("index:")
                    .Append(index.Name.ToLowerInvariant()).Append('|')
                    .Append(index.IsUnique ? "unique" : "normal").Append('|')
                    .AppendJoin(',', index.Columns.Select(column => column.ToLowerInvariant()))
                    .Append('\n');
            }

            foreach (var foreignKey in table.ForeignKeys
                         .OrderBy(item => item.Column, StringComparer.OrdinalIgnoreCase))
            {
                canonical
                    .Append("fk:")
                    .Append(foreignKey.Column.ToLowerInvariant()).Append('|')
                    .Append(foreignKey.ReferencedTable.ToLowerInvariant()).Append('|')
                    .Append(foreignKey.ReferencedColumn.ToLowerInvariant()).Append('|')
                    .Append(foreignKey.DeleteAction.ToLowerInvariant())
                    .Append('\n');
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}

public sealed record DbSchemaMetadata(
    uint SchemaVersion,
    string ProviderId,
    string SchemaFingerprint,
    DbMigrationState MigrationState,
    string? LastMigrationId,
    string? LastError,
    DateTimeOffset UpdatedAt);

public sealed record DbMigrationHistoryEntry(
    string MigrationId,
    uint VersionFrom,
    uint VersionTo,
    string Checksum,
    DateTimeOffset AppliedAt,
    bool Success,
    string? Error);

public sealed record DbCompatibilityReport(
    DbCompatibilityState State,
    uint DeclaredVersion,
    uint ExpectedVersion,
    DbProviderInfo Provider,
    DbSchemaSnapshot ActualSchema,
    DbSchemaSnapshot ExpectedSchema,
    DbSchemaMetadata? Metadata,
    IReadOnlyList<DbCompatibilityIssue> Issues)
{
    public bool IsUsable => State == DbCompatibilityState.Compatible;
    public bool CanMigrate => State == DbCompatibilityState.NeedsMigration;

    public string ToUserMessage()
    {
        var summary = State switch
        {
            DbCompatibilityState.Compatible => "数据库结构和数据版本兼容。",
            DbCompatibilityState.Uninitialized => "数据库尚未初始化。",
            DbCompatibilityState.NeedsMigration =>
                $"数据库需要从 0x{DeclaredVersion:X8} 升级到 0x{ExpectedVersion:X8}。",
            DbCompatibilityState.NewerThanApplication =>
                $"数据库版本 0x{DeclaredVersion:X8} 高于当前程序支持的 0x{ExpectedVersion:X8}。",
            DbCompatibilityState.MigrationUnavailable => "数据库需要升级，但当前 provider 缺少完整迁移链。",
            DbCompatibilityState.SchemaDrift => "数据库实际结构与当前程序的结构契约不一致。",
            DbCompatibilityState.MigrationIncomplete => "检测到上一次数据库迁移未完成。",
            DbCompatibilityState.DataIntegrityError => "数据库内容未通过完整性检查。",
            DbCompatibilityState.ProviderMismatch => "数据库元数据记录的 provider 与当前驱动不匹配。",
            DbCompatibilityState.CapabilityMissing => "数据库 provider 缺少当前程序要求的能力。",
            _ => "数据库兼容性检查失败。",
        };

        var detail = Issues.FirstOrDefault(issue => issue.Severity >= DbIssueSeverity.Error)?.Message;
        return string.IsNullOrWhiteSpace(detail) ? summary : $"{summary} {detail}";
    }
}

public sealed record DbMigrationOptions(bool CreateBackup = true, bool ValidateDataAfterMigration = true);

public sealed record DbMigrationResult(
    bool Success,
    uint VersionFrom,
    uint VersionTo,
    string? BackupPath,
    IReadOnlyList<string> AppliedMigrations,
    DbCompatibilityReport? FinalReport,
    string? Error);
