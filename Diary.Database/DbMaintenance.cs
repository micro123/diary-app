namespace Diary.Database;

[Flags]
public enum DbMaintenanceCapabilities
{
    None = 0,
    Backup = 1,
    Restore = 2,
}

public sealed record DbMaintenanceSupport(
    DbMaintenanceCapabilities Capabilities,
    string? UnavailableReason = null)
{
    public bool CanBackup => Capabilities.HasFlag(DbMaintenanceCapabilities.Backup);
    public bool CanRestore => Capabilities.HasFlag(DbMaintenanceCapabilities.Restore);
}

public sealed record DbBackupResult(
    bool Success,
    string? BackupPath,
    string? Error);

public sealed record DbBackupValidationResult(
    bool Success,
    string ProviderName,
    uint DataVersion,
    DbCompatibilityState? CompatibilityState,
    string? Error);

public sealed record DbRestoreResult(
    bool Success,
    string? RestoredPath,
    string? RecoveryPath,
    bool TargetPreviouslyExisted,
    string? Error);

/// <summary>
/// Provider 可选的数据库维护能力。创建备份要求当前数据库已连接；
/// 执行还原和还原回滚要求当前实例尚未连接。
/// </summary>
public interface IDbMaintenanceProvider
{
    DbMaintenanceSupport GetMaintenanceSupport();

    DbBackupResult CreateBackup(string destinationPath);

    DbBackupValidationResult ValidateBackup(string backupPath, uint expectedVersion);

    DbRestoreResult RestoreBackup(string backupPath, uint expectedVersion);

    bool RollbackRestore(DbRestoreResult restore, out string? error);
}
