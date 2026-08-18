using System.Text.Json;
using Diary.Database;
using Diary.Utils;

namespace Diary.App.Services;

internal sealed record PendingDatabaseRestoreContext(
    IDbMaintenanceProvider Provider,
    DbRestoreResult RestoreResult);

internal sealed record DatabaseRestoreStageResult(
    bool Success,
    string? StagedBackupPath,
    string? Error);

internal sealed class DatabaseRestoreCoordinator
{
    private const string DescriptorFileName = "pending-restore.json";
    private const string StagedBackupFileName = "pending-backup.sqlite3";
    private readonly string _restoreDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public DatabaseRestoreCoordinator()
        : this(Path.Combine(FsTools.GetApplicationDataDirectory(), "DatabaseRestore"))
    {
    }

    internal DatabaseRestoreCoordinator(string restoreDirectory)
    {
        _restoreDirectory = restoreDirectory;
    }

    public DatabaseRestoreStageResult Stage(string providerName, string backupPath)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            return new DatabaseRestoreStageResult(false, null, "数据库 provider 名称为空。");
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            return new DatabaseRestoreStageResult(false, null, "待还原的备份文件不存在。");

        var stagedPath = Path.Combine(_restoreDirectory, StagedBackupFileName);
        var stagedTemporaryPath = stagedPath + ".tmp";
        try
        {
            Directory.CreateDirectory(_restoreDirectory);
            File.Copy(backupPath, stagedTemporaryPath, true);
            File.Move(stagedTemporaryPath, stagedPath, true);
            WriteDescriptor(new PendingRestoreDescriptor(
                providerName,
                stagedPath,
                DateTimeOffset.UtcNow,
                PendingRestoreState.Pending,
                null));
            return new DatabaseRestoreStageResult(true, stagedPath, null);
        }
        catch (Exception exception)
        {
            TryDelete(stagedTemporaryPath);
            return new DatabaseRestoreStageResult(false, null, $"暂存数据库还原失败：{exception.Message}");
        }
    }

    public bool TryApplyPending(
        string providerName,
        IDbMaintenanceProvider? provider,
        uint expectedVersion,
        out PendingDatabaseRestoreContext? context,
        out string? error)
    {
        context = null;
        error = null;
        if (!File.Exists(DescriptorPath))
            return true;

        PendingRestoreDescriptor descriptor;
        try
        {
            descriptor = ReadDescriptor();
        }
        catch (Exception exception)
        {
            error = $"读取待还原数据库信息失败：{exception.Message}";
            return false;
        }

        if (!string.Equals(descriptor.ProviderName, providerName, StringComparison.Ordinal))
        {
            error = $"存在为 {descriptor.ProviderName} 暂存的数据库还原，但当前驱动为 {providerName}。";
            return false;
        }
        if (provider is null)
        {
            error = $"数据库驱动 {providerName} 不支持应用内还原。";
            return false;
        }

        if (descriptor.State == PendingRestoreState.Applied && descriptor.RestoreResult is not null)
        {
            if (!provider.RollbackRestore(descriptor.RestoreResult, out var rollbackError))
            {
                error = $"上次数据库还原未完成，且无法回滚：{rollbackError}";
                return false;
            }
            descriptor = descriptor with
            {
                State = PendingRestoreState.Pending,
                RestoreResult = null,
            };
            WriteDescriptor(descriptor);
        }

        if (!File.Exists(descriptor.StagedBackupPath))
        {
            error = "暂存的数据库备份文件不存在。";
            return false;
        }

        var validation = provider.ValidateBackup(descriptor.StagedBackupPath, expectedVersion);
        if (!validation.Success)
        {
            error = $"待还原数据库校验失败：{validation.Error}";
            return false;
        }

        var restore = provider.RestoreBackup(descriptor.StagedBackupPath, expectedVersion);
        if (!restore.Success)
        {
            error = restore.Error;
            return false;
        }

        WriteDescriptor(descriptor with
        {
            State = PendingRestoreState.Applied,
            RestoreResult = restore,
        });
        context = new PendingDatabaseRestoreContext(provider, restore);
        return true;
    }

    public void Complete(PendingDatabaseRestoreContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        CleanupPendingFiles();
    }

    public bool Rollback(PendingDatabaseRestoreContext context, out string? error)
    {
        ArgumentNullException.ThrowIfNull(context);
        var success = context.Provider.RollbackRestore(context.RestoreResult, out error);
        CleanupPendingFiles();
        return success;
    }

    private string DescriptorPath => Path.Combine(_restoreDirectory, DescriptorFileName);

    private PendingRestoreDescriptor ReadDescriptor()
    {
        var json = File.ReadAllText(DescriptorPath);
        return JsonSerializer.Deserialize<PendingRestoreDescriptor>(json, _jsonOptions)
               ?? throw new InvalidDataException("待还原数据库描述为空。");
    }

    private void WriteDescriptor(PendingRestoreDescriptor descriptor)
    {
        Directory.CreateDirectory(_restoreDirectory);
        var temporaryPath = DescriptorPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(descriptor, _jsonOptions));
        File.Move(temporaryPath, DescriptorPath, true);
    }

    private void CleanupPendingFiles()
    {
        try
        {
            if (File.Exists(DescriptorPath))
            {
                var descriptor = ReadDescriptor();
                TryDelete(descriptor.StagedBackupPath);
            }
        }
        catch (Exception)
        {
        }
        TryDelete(DescriptorPath);
        TryDelete(DescriptorPath + ".tmp");
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

    private enum PendingRestoreState
    {
        Pending,
        Applied,
    }

    private sealed record PendingRestoreDescriptor(
        string ProviderName,
        string StagedBackupPath,
        DateTimeOffset CreatedAt,
        PendingRestoreState State,
        DbRestoreResult? RestoreResult);
}
