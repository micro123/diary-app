using System.Data.SQLite;
using Diary.Database;

namespace Diary.Db.SQLite;

public sealed partial class SQLiteDb : IDbMaintenanceProvider
{
    public DbMaintenanceSupport GetMaintenanceSupport()
    {
        var config = Factory.GetConfig() as Config;
        if (config is null || string.IsNullOrWhiteSpace(config.FilePath))
            return new DbMaintenanceSupport(DbMaintenanceCapabilities.None, "SQLite 数据库路径未配置。");
        if (string.Equals(config.FilePath, ":memory:", StringComparison.OrdinalIgnoreCase))
            return new DbMaintenanceSupport(DbMaintenanceCapabilities.None, "内存 SQLite 数据库不支持备份和还原。");
        return new DbMaintenanceSupport(
            DbMaintenanceCapabilities.Backup | DbMaintenanceCapabilities.Restore);
    }

    public DbBackupResult CreateBackup(string destinationPath)
    {
        var support = GetMaintenanceSupport();
        if (!support.CanBackup)
            return new DbBackupResult(false, null, support.UnavailableReason);
        if (_connection is null)
            return new DbBackupResult(false, null, "SQLite 数据库尚未连接。");
        if (string.IsNullOrWhiteSpace(destinationPath))
            return new DbBackupResult(false, null, "未指定备份文件路径。");

        var config = (Config)Factory.GetConfig();
        var sourcePath = Path.GetFullPath(config.FilePath);
        var finalPath = Path.GetFullPath(destinationPath);
        if (string.Equals(sourcePath, finalPath, StringComparison.OrdinalIgnoreCase))
            return new DbBackupResult(false, null, "备份文件不能覆盖当前数据库。");

        var destinationDirectory = Path.GetDirectoryName(finalPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            return new DbBackupResult(false, null, "无法确定备份文件所在目录。");

        var temporaryPath = finalPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(destinationDirectory);
            using (var destination = new SQLiteConnection(
                       new SQLiteConnectionStringBuilder { DataSource = temporaryPath }.ToString()))
            {
                destination.Open();
                _connection.BackupDatabase(destination, "main", "main", -1, null, 0);
            }

            if (!QuickCheck(temporaryPath, out var validationError))
                throw new InvalidDataException(validationError);

            File.Move(temporaryPath, finalPath, true);
            return new DbBackupResult(true, finalPath, null);
        }
        catch (Exception exception)
        {
            TryDelete(temporaryPath);
            return new DbBackupResult(false, null, $"SQLite 备份创建失败：{exception.Message}");
        }
    }

    public DbBackupValidationResult ValidateBackup(string backupPath, uint expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(backupPath))
            return InvalidBackup("未指定备份文件路径。");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(backupPath);
        }
        catch (Exception exception)
        {
            return InvalidBackup($"备份文件路径无效：{exception.Message}");
        }

        if (!File.Exists(fullPath))
            return InvalidBackup("备份文件不存在。");
        if (!QuickCheck(fullPath, out var quickCheckError))
            return InvalidBackup(quickCheckError);

        try
        {
            using var validationDb = new SQLiteDb(new MaintenanceFactory(fullPath, Factory));
            if (!validationDb.Connect())
                return InvalidBackup("无法打开 SQLite 备份文件。");

            var report = validationDb.CheckCompatibility(expectedVersion);
            if (report.State is not (DbCompatibilityState.Compatible or DbCompatibilityState.NeedsMigration))
            {
                return new DbBackupValidationResult(
                    false,
                    "SQLite",
                    report.DeclaredVersion,
                    report.State,
                    report.ToUserMessage());
            }

            return new DbBackupValidationResult(
                true,
                "SQLite",
                report.DeclaredVersion,
                report.State,
                null);
        }
        catch (Exception exception)
        {
            return InvalidBackup($"SQLite 备份校验失败：{exception.Message}");
        }
    }

    public DbRestoreResult RestoreBackup(string backupPath, uint expectedVersion)
    {
        var support = GetMaintenanceSupport();
        if (!support.CanRestore)
            return FailedRestore(support.UnavailableReason);
        if (_connection is not null)
            return FailedRestore("执行 SQLite 还原前必须关闭当前数据库连接。");

        var validation = ValidateBackup(backupPath, expectedVersion);
        if (!validation.Success)
            return FailedRestore(validation.Error);

        var config = (Config)Factory.GetConfig();
        var targetPath = Path.GetFullPath(config.FilePath);
        var sourcePath = Path.GetFullPath(backupPath);
        if (string.Equals(targetPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            return FailedRestore("备份文件不能与当前数据库使用同一路径。");

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(targetDirectory))
            return FailedRestore("无法确定 SQLite 数据库所在目录。");

        var restoreTemporaryPath = targetPath + $".{Guid.NewGuid():N}.restore.tmp";
        var backupDirectory = Path.Combine(targetDirectory, "Backups");
        var recoveryPath = Path.Combine(
            backupDirectory,
            $"{Path.GetFileName(targetPath)}.before-restore.{DateTimeOffset.Now:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.bak");
        var targetPreviouslyExisted = File.Exists(targetPath);

        try
        {
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateDirectory(backupDirectory);
            File.Copy(sourcePath, restoreTemporaryPath, true);
            if (!QuickCheck(restoreTemporaryPath, out var temporaryValidationError))
                throw new InvalidDataException(temporaryValidationError);

            if (targetPreviouslyExisted)
            {
                File.Move(targetPath, recoveryPath);
                MoveSidecar(targetPath, recoveryPath, "-wal");
                MoveSidecar(targetPath, recoveryPath, "-shm");
            }

            File.Move(restoreTemporaryPath, targetPath);
            return new DbRestoreResult(true, targetPath, recoveryPath, targetPreviouslyExisted, null);
        }
        catch (Exception exception)
        {
            TryDelete(restoreTemporaryPath);
            TryRestoreOriginal(targetPath, recoveryPath, targetPreviouslyExisted);
            return FailedRestore($"SQLite 还原失败：{exception.Message}");
        }
    }

    public bool RollbackRestore(DbRestoreResult restore, out string? error)
    {
        error = null;
        if (_connection is not null)
        {
            error = "回滚 SQLite 还原前必须关闭当前数据库连接。";
            return false;
        }
        if (!restore.Success || string.IsNullOrWhiteSpace(restore.RestoredPath))
        {
            error = "没有可回滚的 SQLite 还原记录。";
            return false;
        }

        try
        {
            TryDelete(restore.RestoredPath);
            TryDelete(restore.RestoredPath + "-wal");
            TryDelete(restore.RestoredPath + "-shm");
            if (restore.TargetPreviouslyExisted)
            {
                if (string.IsNullOrWhiteSpace(restore.RecoveryPath) || !File.Exists(restore.RecoveryPath))
                    throw new FileNotFoundException("还原前安全副本不存在。", restore.RecoveryPath);
                File.Move(restore.RecoveryPath, restore.RestoredPath);
                MoveSidecar(restore.RecoveryPath, restore.RestoredPath, "-wal");
                MoveSidecar(restore.RecoveryPath, restore.RestoredPath, "-shm");
            }
            return true;
        }
        catch (Exception exception)
        {
            error = $"SQLite 还原回滚失败：{exception.Message}";
            return false;
        }
    }

    private static bool QuickCheck(string path, out string? error)
    {
        error = null;
        try
        {
            var builder = new SQLiteConnectionStringBuilder { DataSource = path };
            using var connection = new SQLiteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(command.ExecuteScalar());
            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                return true;
            error = $"SQLite 完整性检查失败：{result ?? "未返回结果"}";
            return false;
        }
        catch (Exception exception)
        {
            error = $"无法校验 SQLite 文件：{exception.Message}";
            return false;
        }
    }

    private static DbBackupValidationResult InvalidBackup(string? error)
        => new(false, "SQLite", 0, null, error ?? "SQLite 备份无效。");

    private static DbRestoreResult FailedRestore(string? error)
        => new(false, null, null, false, error ?? "SQLite 还原失败。");

    private static void MoveSidecar(string sourceBase, string targetBase, string suffix)
    {
        var source = sourceBase + suffix;
        if (File.Exists(source))
            File.Move(source, targetBase + suffix, true);
    }

    private static void TryRestoreOriginal(string targetPath, string recoveryPath, bool targetPreviouslyExisted)
    {
        try
        {
            TryDelete(targetPath);
            if (!targetPreviouslyExisted || !File.Exists(recoveryPath))
                return;
            File.Move(recoveryPath, targetPath);
            MoveSidecar(recoveryPath, targetPath, "-wal");
            MoveSidecar(recoveryPath, targetPath, "-shm");
        }
        catch (Exception)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private sealed class MaintenanceFactory(string path, IDbFactory source) : IDbFactory
    {
        private readonly Config _config = new() { FilePath = path };

        public string Name => source.Name;
        public bool Usable => true;
        public DbInterfaceBase Create() => new SQLiteDb(this);
        public Migration? GetMigration(uint version) => source.GetMigration(version);
        public object GetConfig() => _config;
    }
}
