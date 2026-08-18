namespace Diary.Database;

public abstract partial class DbInterfaceBase
{
    private static readonly IReadOnlySet<DbCapability> RequiredCapabilities =
        new HashSet<DbCapability>
        {
            DbCapability.Transactions,
            DbCapability.ForeignKeys,
            DbCapability.UniqueIndexes,
        };

    /// <summary>
    /// 返回 provider、服务端版本和实际可用能力。
    /// </summary>
    public abstract DbProviderInfo GetProviderInfo();

    /// <summary>
    /// 从数据库系统目录读取实际结构。该快照不应通过应用初始化脚本推断。
    /// </summary>
    public abstract DbSchemaSnapshot InspectSchema();

    /// <summary>
    /// 当前应用版本要求的逻辑核心结构。provider 可以覆盖以声明 provider 特有契约。
    /// </summary>
    public virtual DbSchemaSnapshot ExpectedSchema => CoreSchemaContract.Current;

    /// <summary>
    /// 检查业务数据完整性。结构检查通过后才执行，避免在空库或半初始化库上制造噪音。
    /// </summary>
    protected virtual IReadOnlyList<DbCompatibilityIssue> CheckDataIntegrity() => [];

    protected abstract DbSchemaMetadata? ReadSchemaMetadata();
    protected abstract bool WriteSchemaMetadata(DbSchemaMetadata metadata);
    protected abstract bool RecordMigrationHistory(DbMigrationHistoryEntry entry);

    public DbCompatibilityReport CheckCompatibility(
        uint expectedVersion,
        bool allowInProgressMigration = false)
        => CheckCompatibility(expectedVersion, allowInProgressMigration, validateDataIntegrity: true);

    public DbCompatibilityReport CheckCompatibility(
        uint expectedVersion,
        bool allowInProgressMigration,
        bool validateDataIntegrity)
    {
        DbProviderInfo provider;
        DbSchemaSnapshot actualSchema;
        uint declaredVersion;
        DbSchemaMetadata? metadata;

        try
        {
            provider = GetProviderInfo();
            actualSchema = InspectSchema();
            declaredVersion = actualSchema.Tables.Count == 0 ? 0 : GetDataVersion();
            metadata = actualSchema.Tables.Count == 0 ? null : ReadSchemaMetadata();
        }
        catch (Exception exception)
        {
            var unavailableProvider = new DbProviderInfo(
                Factory.Name,
                "unknown",
                new HashSet<DbCapability>());
            var emptySchema = new DbSchemaSnapshot([]);
            return new DbCompatibilityReport(
                DbCompatibilityState.Unavailable,
                0,
                expectedVersion,
                unavailableProvider,
                emptySchema,
                ExpectedSchema,
                null,
                [new DbCompatibilityIssue(
                    "DB-COMPATIBILITY-CHECK-FAILED",
                    DbIssueSeverity.Blocking,
                    $"数据库兼容性检查失败：{exception.Message}",
                    SuggestedAction: "检查连接、权限和数据库诊断日志。")]);
        }

        var issues = new List<DbCompatibilityIssue>();
        if (!string.Equals(provider.ProviderId, Factory.Name, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new DbCompatibilityIssue(
                "DB-PROVIDER-MISMATCH",
                DbIssueSeverity.Blocking,
                $"数据库声明 provider 为“{provider.ProviderId}”，当前驱动为“{Factory.Name}”。",
                SuggestedAction: "使用创建该数据库的 provider，或先执行明确的数据迁移。"));
        }

        foreach (var capability in RequiredCapabilities)
        {
            if (!provider.Capabilities.Contains(capability))
            {
                issues.Add(new DbCompatibilityIssue(
                    "DB-CAPABILITY-MISSING",
                    DbIssueSeverity.Blocking,
                    $"当前数据库不支持必需能力：{capability}。",
                    SuggestedAction: "升级数据库服务或更换支持该能力的 provider。"));
            }
        }

        if (actualSchema.Tables.Count == 0)
        {
            issues.Add(new DbCompatibilityIssue(
                "DB-SCHEMA-EMPTY",
                DbIssueSeverity.Information,
                "数据库中尚未发现 Diary 核心表。",
                SuggestedAction: "执行数据库初始化。"));
            return new DbCompatibilityReport(
                DbCompatibilityState.Uninitialized,
                declaredVersion,
                expectedVersion,
                provider,
                actualSchema,
                ExpectedSchema,
                metadata,
                issues);
        }

        if (declaredVersion > expectedVersion)
        {
            issues.Add(new DbCompatibilityIssue(
                "DB-VERSION-NEWER",
                DbIssueSeverity.Blocking,
                $"数据库版本 0x{declaredVersion:X8} 高于当前程序版本 0x{expectedVersion:X8}。",
                SuggestedAction: "升级应用后再打开该数据库，避免降级写入。"));
        }

        if (metadata is not null)
        {
            if (!string.Equals(metadata.ProviderId, provider.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new DbCompatibilityIssue(
                    "DB-METADATA-PROVIDER-MISMATCH",
                    DbIssueSeverity.Blocking,
                    $"数据库元数据记录 provider 为“{metadata.ProviderId}”，实际 provider 为“{provider.ProviderId}”。",
                    SuggestedAction: "使用正确的数据库驱动。"));
            }

            if (metadata.SchemaVersion != declaredVersion)
            {
                issues.Add(new DbCompatibilityIssue(
                    "DB-METADATA-VERSION-MISMATCH",
                    DbIssueSeverity.Blocking,
                    $"数据库元数据版本 0x{metadata.SchemaVersion:X8} 与版本游标 0x{declaredVersion:X8} 不一致。",
                    SuggestedAction: "不要手动修改版本表，检查上次迁移状态或从备份恢复。"));
            }

            if (metadata.MigrationState == DbMigrationState.Stable &&
                string.IsNullOrWhiteSpace(metadata.SchemaFingerprint))
            {
                issues.Add(new DbCompatibilityIssue(
                    "DB-METADATA-FINGERPRINT-MISSING",
                    DbIssueSeverity.Blocking,
                    "数据库已标记为 Stable，但没有保存结构指纹。",
                    SuggestedAction: "重新执行兼容性检查并写入元数据，或恢复可信备份。"));
            }

            if (!allowInProgressMigration &&
                (metadata.MigrationState is DbMigrationState.Running or DbMigrationState.Failed))
            {
                issues.Add(new DbCompatibilityIssue(
                    "DB-MIGRATION-INCOMPLETE",
                    DbIssueSeverity.Blocking,
                    string.IsNullOrWhiteSpace(metadata.LastError)
                        ? "数据库上一次迁移没有进入稳定状态。"
                        : $"数据库上一次迁移未完成：{metadata.LastError}",
                    SuggestedAction: "恢复迁移前备份，或确认迁移步骤后重新执行。"));
            }

            if (!string.IsNullOrWhiteSpace(metadata.SchemaFingerprint) &&
                !string.Equals(metadata.SchemaFingerprint, actualSchema.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new DbCompatibilityIssue(
                    "DB-SCHEMA-FINGERPRINT-MISMATCH",
                    DbIssueSeverity.Blocking,
                    "数据库结构指纹与上次记录不一致，可能存在手工改表或未登记的迁移。",
                    SuggestedAction: "不要直接写入数据库，先执行结构诊断或从备份恢复。"));
            }
        }

        CompareSchema(ExpectedSchema, actualSchema, issues);

        var state = DetermineState(
            declaredVersion,
            expectedVersion,
            issues,
            metadata,
            declaredVersion < expectedVersion && HasMigrationChain(declaredVersion, expectedVersion),
            allowInProgressMigration);

        if (state is DbCompatibilityState.Compatible && validateDataIntegrity)
        {
            issues.AddRange(CheckDataIntegrity());
            if (issues.Any(issue => issue.Severity == DbIssueSeverity.Blocking))
                state = DbCompatibilityState.DataIntegrityError;
        }

        return new DbCompatibilityReport(
            state,
            declaredVersion,
            expectedVersion,
            provider,
            actualSchema,
            ExpectedSchema,
            metadata,
            issues);
    }

    public bool PersistCompatibilityMetadata(DbCompatibilityReport report)
    {
        if (!report.IsUsable)
            return false;
        return WriteSchemaMetadata(new DbSchemaMetadata(
            report.DeclaredVersion,
            report.Provider.ProviderId,
            report.ActualSchema.Fingerprint,
            DbMigrationState.Stable,
            null,
            null,
            DateTimeOffset.UtcNow));
    }

    public DbMigrationResult MigrateTo(uint targetVersion, DbMigrationOptions? options = null)
    {
        options ??= new DbMigrationOptions();
        var before = CheckCompatibility(targetVersion);
        if (before.State == DbCompatibilityState.Compatible)
        {
            if (!PersistCompatibilityMetadata(before))
            {
                const string error = "无法写入数据库兼容性元数据。";
                return new DbMigrationResult(
                    false,
                    before.DeclaredVersion,
                    before.DeclaredVersion,
                    null,
                    [],
                    before,
                    error);
            }
            return new DbMigrationResult(true, before.DeclaredVersion, before.DeclaredVersion, null, [], before, null);
        }

        if (before.State != DbCompatibilityState.NeedsMigration)
        {
            return new DbMigrationResult(
                false,
                before.DeclaredVersion,
                targetVersion,
                null,
                [],
                before,
                before.ToUserMessage());
        }

        string? backupPath = null;
        if (options.CreateBackup && !TryCreateMigrationBackup(targetVersion, out backupPath, out var backupError))
        {
            var error = $"数据库升级前备份失败：{backupError}";
            MarkMigrationFailed(before.DeclaredVersion, null, error);
            return new DbMigrationResult(false, before.DeclaredVersion, targetVersion, null, [], before, error);
        }

        var applied = new List<string>();
        var currentVersion = before.DeclaredVersion;
        if (!MarkMigrationRunning(currentVersion, null, null))
        {
            const string error = "无法记录数据库迁移开始状态。";
            return new DbMigrationResult(false, before.DeclaredVersion, targetVersion, backupPath, applied, null, error);
        }

        while (currentVersion < targetVersion)
        {
            var migration = Factory.GetMigration(currentVersion);
            if (migration is null ||
                migration.VersionFrom != currentVersion ||
                migration.VersionTo <= currentVersion ||
                migration.VersionTo > targetVersion)
            {
                var error = $"从 0x{currentVersion:X8} 到 0x{targetVersion:X8} 的迁移链不完整。";
                MarkMigrationFailed(currentVersion, null, error);
                return new DbMigrationResult(false, before.DeclaredVersion, targetVersion, backupPath, applied, null, error);
            }

            var migrationStarted = false;
            var committed = false;
            try
            {
                MarkMigrationRunning(currentVersion, migration.Id, null);
                migrationStarted = BeginTransaction();
                if (!migrationStarted || !migration.Up(this))
                    throw new InvalidOperationException("迁移脚本返回失败。");

                var migratedVersion = GetDataVersion();
                if (migratedVersion != migration.VersionTo)
                    throw new InvalidOperationException(
                        $"迁移 {migration.Id} 执行后版本为 0x{migratedVersion:X8}，预期为 0x{migration.VersionTo:X8}。");

                if (!RecordMigrationHistory(new DbMigrationHistoryEntry(
                        migration.Id,
                        migration.VersionFrom,
                        migration.VersionTo,
                        migration.Checksum,
                        DateTimeOffset.UtcNow,
                        true,
                        null)))
                {
                    throw new InvalidOperationException("无法写入迁移历史。");
                }

                committed = CommitTransaction();
                if (!committed)
                    throw new InvalidOperationException("提交迁移事务失败。");

                currentVersion = migratedVersion;
                applied.Add(migration.Id);
                if (!MarkMigrationRunning(currentVersion, null, null))
                    throw new InvalidOperationException("无法更新数据库迁移状态。");
            }
            catch (Exception exception)
            {
                var error = $"迁移 {migration.Id} 失败：{exception.Message}";
                if (migrationStarted && !committed)
                {
                    try
                    {
                        RollbackTransaction();
                    }
                    catch (Exception)
                    {
                    }
                    migrationStarted = false;
                }
                try
                {
                    RecordMigrationHistory(new DbMigrationHistoryEntry(
                        migration.Id,
                        migration.VersionFrom,
                        migration.VersionTo,
                        migration.Checksum,
                        DateTimeOffset.UtcNow,
                        false,
                        error));
                }
                catch (Exception)
                {
                }
                MarkMigrationFailed(currentVersion, migration.Id, error);
                return new DbMigrationResult(false, before.DeclaredVersion, targetVersion, backupPath, applied, null, error);
            }
            finally
            {
                if (migrationStarted && !committed)
                {
                    try
                    {
                        RollbackTransaction();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        var final = CheckCompatibility(
            targetVersion,
            allowInProgressMigration: true,
            validateDataIntegrity: options.ValidateDataAfterMigration);
        if (final.State != DbCompatibilityState.Compatible)
        {
            var error = $"迁移完成后兼容性复检失败：{final.ToUserMessage()}";
            MarkMigrationFailed(currentVersion, applied.LastOrDefault(), error);
            return new DbMigrationResult(false, before.DeclaredVersion, targetVersion, backupPath, applied, final, error);
        }

        if (!PersistStableMetadata(final))
        {
            const string error = "迁移完成，但无法写入稳定的数据库兼容性元数据。";
            MarkMigrationFailed(currentVersion, applied.LastOrDefault(), error);
            return new DbMigrationResult(false, before.DeclaredVersion, currentVersion, backupPath, applied, final, error);
        }
        return new DbMigrationResult(true, before.DeclaredVersion, currentVersion, backupPath, applied, final, null);
    }

    private void CompareSchema(
        DbSchemaSnapshot expected,
        DbSchemaSnapshot actual,
        ICollection<DbCompatibilityIssue> issues)
    {
        foreach (var expectedTable in expected.Tables)
        {
            var actualTable = actual.FindTable(expectedTable.Name);
            if (actualTable is null)
            {
                issues.Add(new DbCompatibilityIssue(
                    "DB-SCHEMA-TABLE-MISSING",
                    DbIssueSeverity.Blocking,
                    $"缺少必需的数据表：{expectedTable.Name}。",
                    expectedTable.Name,
                    "从备份恢复，或执行针对当前版本的结构迁移。"));
                continue;
            }

            foreach (var expectedColumn in expectedTable.Columns)
            {
                var actualColumn = actualTable.Columns.FirstOrDefault(column =>
                    string.Equals(column.Name, expectedColumn.Name, StringComparison.OrdinalIgnoreCase));
                if (actualColumn is null)
                {
                    issues.Add(new DbCompatibilityIssue(
                        "DB-SCHEMA-COLUMN-MISSING",
                        DbIssueSeverity.Blocking,
                        $"数据表 {expectedTable.Name} 缺少必需字段：{expectedColumn.Name}。",
                        $"{expectedTable.Name}.{expectedColumn.Name}",
                        "不要依赖初始化脚本自动补齐，登记并执行正式迁移。"));
                    continue;
                }

                if (!string.Equals(actualColumn.LogicalType, expectedColumn.LogicalType, StringComparison.OrdinalIgnoreCase) ||
                    actualColumn.IsNullable != expectedColumn.IsNullable ||
                    actualColumn.IsPrimaryKey != expectedColumn.IsPrimaryKey)
                {
                    issues.Add(new DbCompatibilityIssue(
                        "DB-SCHEMA-COLUMN-MISMATCH",
                        DbIssueSeverity.Blocking,
                        $"字段 {expectedTable.Name}.{expectedColumn.Name} 的类型、可空性或主键属性不符合契约。",
                        $"{expectedTable.Name}.{expectedColumn.Name}",
                        "检查 provider 迁移和数据库实际结构。"));
                }
            }

            foreach (var expectedIndex in expectedTable.Indexes)
            {
                var actualIndex = actualTable.Indexes.FirstOrDefault(index =>
                    string.Equals(index.Name, expectedIndex.Name, StringComparison.OrdinalIgnoreCase));
                if (actualIndex is null ||
                    actualIndex.IsUnique != expectedIndex.IsUnique ||
                    !actualIndex.Columns.SequenceEqual(expectedIndex.Columns, StringComparer.OrdinalIgnoreCase))
                {
                    issues.Add(new DbCompatibilityIssue(
                        "DB-SCHEMA-INDEX-MISSING",
                        DbIssueSeverity.Blocking,
                        $"数据表 {expectedTable.Name} 缺少或错误的索引：{expectedIndex.Name}。",
                        expectedIndex.Name,
                        "重新执行对应 provider 的正式迁移。"));
                }
            }

            foreach (var expectedForeignKey in expectedTable.ForeignKeys)
            {
                var actualForeignKey = actualTable.ForeignKeys.FirstOrDefault(foreignKey =>
                    string.Equals(foreignKey.Column, expectedForeignKey.Column, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(foreignKey.ReferencedTable, expectedForeignKey.ReferencedTable, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(foreignKey.ReferencedColumn, expectedForeignKey.ReferencedColumn, StringComparison.OrdinalIgnoreCase));
                if (actualForeignKey is null)
                {
                    issues.Add(new DbCompatibilityIssue(
                        "DB-SCHEMA-FOREIGN-KEY-MISSING",
                        DbIssueSeverity.Blocking,
                        $"数据表 {expectedTable.Name} 缺少外键：{expectedForeignKey.Column} -> " +
                        $"{expectedForeignKey.ReferencedTable}.{expectedForeignKey.ReferencedColumn}。",
                        expectedTable.Name,
                        "检查数据库约束并执行正式迁移。"));
                }
            }
        }
    }

    private DbCompatibilityState DetermineState(
        uint declaredVersion,
        uint expectedVersion,
        IReadOnlyCollection<DbCompatibilityIssue> issues,
        DbSchemaMetadata? metadata,
        bool migrationChainAvailable,
        bool allowInProgressMigration)
    {
        if (issues.Any(issue => issue.Code is "DB-PROVIDER-MISMATCH" or "DB-METADATA-PROVIDER-MISMATCH"))
            return DbCompatibilityState.ProviderMismatch;
        if (issues.Any(issue => issue.Code == "DB-CAPABILITY-MISSING"))
            return DbCompatibilityState.CapabilityMissing;
        if (issues.Any(issue => issue.Code is "DB-SCHEMA-FINGERPRINT-MISMATCH" or "DB-METADATA-VERSION-MISMATCH"))
            return DbCompatibilityState.SchemaDrift;
        if (declaredVersion > expectedVersion)
            return DbCompatibilityState.NewerThanApplication;
        if (!allowInProgressMigration &&
            (metadata?.MigrationState is DbMigrationState.Running or DbMigrationState.Failed))
            return DbCompatibilityState.MigrationIncomplete;
        if (declaredVersion < expectedVersion)
            return migrationChainAvailable
                ? DbCompatibilityState.NeedsMigration
                : DbCompatibilityState.MigrationUnavailable;
        if (issues.Any(issue => issue.Code.StartsWith("DB-SCHEMA-", StringComparison.Ordinal)))
            return DbCompatibilityState.SchemaDrift;
        return issues.Any(issue => issue.Severity == DbIssueSeverity.Blocking)
            ? DbCompatibilityState.SchemaDrift
            : DbCompatibilityState.Compatible;
    }

    private bool HasMigrationChain(uint currentVersion, uint targetVersion)
    {
        var visited = new HashSet<uint>();
        while (currentVersion < targetVersion && visited.Add(currentVersion))
        {
            var migration = Factory.GetMigration(currentVersion);
            if (migration is null ||
                migration.VersionFrom != currentVersion ||
                migration.VersionTo <= currentVersion ||
                migration.VersionTo > targetVersion)
                return false;
            currentVersion = migration.VersionTo;
        }

        return currentVersion == targetVersion;
    }

    private bool PersistStableMetadata(DbCompatibilityReport report)
    {
        return WriteSchemaMetadata(new DbSchemaMetadata(
            report.DeclaredVersion,
            report.Provider.ProviderId,
            report.ActualSchema.Fingerprint,
            DbMigrationState.Stable,
            null,
            null,
            DateTimeOffset.UtcNow));
    }

    private bool MarkMigrationRunning(uint version, string? migrationId, string? error)
    {
        return WriteSchemaMetadata(new DbSchemaMetadata(
            version,
            Factory.Name,
            string.Empty,
            DbMigrationState.Running,
            migrationId,
            error,
            DateTimeOffset.UtcNow));
    }

    private bool MarkMigrationFailed(uint version, string? migrationId, string error)
    {
        return WriteSchemaMetadata(new DbSchemaMetadata(
            version,
            Factory.Name,
            string.Empty,
            DbMigrationState.Failed,
            migrationId,
            error,
            DateTimeOffset.UtcNow));
    }
}
