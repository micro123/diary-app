using System.Diagnostics;

namespace Diary.Update;

public sealed class UpdateTransactionExecutor
{
    public async ValueTask<UpdateApplyResult> ApplyAsync(
        ValidatedUpdatePlan plan,
        bool restartApplication = true,
        CancellationToken cancellationToken = default)
    {
        var store = new UpdateTransactionStore(plan);
        await using var transactionLock = await store.AcquireLockAsync(cancellationToken);
        var currentStatus = await store.ReadStatusAsync(cancellationToken);
        if (currentStatus?.State is UpdateTransactionState.Applying or UpdateTransactionState.RollingBack)
            throw new InvalidOperationException("事务处于未恢复状态，请先执行 --recover。");
        if (currentStatus?.State is UpdateTransactionState.Applied
            or UpdateTransactionState.Restarted
            or UpdateTransactionState.Confirmed)
            return new(currentStatus.State, currentStatus.State == UpdateTransactionState.Restarted);

        Directory.CreateDirectory(plan.BackupDirectory);
        store.ResetJournal();
        await store.WriteStatusAsync(UpdateTransactionState.Applying, cancellationToken: cancellationToken);

        try
        {
            await PreflightAsync(plan, cancellationToken);
            await BackupExistingFilesAsync(plan, cancellationToken);
            var sequence = 0;
            foreach (var operation in plan.Operations.Where(item => item.Operation.Kind != UpdateFileOperationKind.Delete))
                await ApplyOperationAsync(plan, store, operation, sequence++, cancellationToken);
            foreach (var operation in plan.Operations.Where(item => item.Operation.Kind == UpdateFileOperationKind.Delete))
                await ApplyOperationAsync(plan, store, operation, sequence++, cancellationToken);
            await ApplyInstalledManifestAsync(plan, store, sequence, cancellationToken);
            await store.WriteStatusAsync(UpdateTransactionState.Applied, cancellationToken: cancellationToken);

        }
        catch (Exception applyException)
        {
            try
            {
                await store.WriteStatusAsync(UpdateTransactionState.RollingBack, applyException.Message, CancellationToken.None);
                await RollbackAsync(plan, store, CancellationToken.None);
                await store.WriteStatusAsync(UpdateTransactionState.RolledBack, applyException.Message, CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                await store.WriteStatusAsync(UpdateTransactionState.Failed, rollbackException.Message, CancellationToken.None);
                throw new AggregateException("更新失败且回滚未完成。", applyException, rollbackException);
            }
            throw;
        }

        if (!restartApplication || plan.Plan.Restart is null)
            return new(UpdateTransactionState.Applied, false);
        try
        {
            StartApplication(plan, plan.Plan.Restart.Sha256);
            await store.WriteStatusAsync(UpdateTransactionState.Restarted, cancellationToken: cancellationToken);
            return new(UpdateTransactionState.Restarted, true);
        }
        catch (Exception restartException)
        {
            await store.WriteStatusAsync(
                UpdateTransactionState.Applied,
                $"文件更新成功，但应用重启失败：{restartException.Message}",
                CancellationToken.None);
            return new(UpdateTransactionState.Applied, false);
        }
    }

    public async ValueTask<UpdateTransactionState> RecoverAsync(
        ValidatedUpdatePlan plan,
        bool rollbackApplied = false,
        CancellationToken cancellationToken = default)
    {
        var store = new UpdateTransactionStore(plan);
        await using var transactionLock = await store.AcquireLockAsync(cancellationToken);
        var status = await store.ReadStatusAsync(cancellationToken);
        if (status is null)
            throw new InvalidOperationException("事务没有可恢复状态。");
        if (status.State is UpdateTransactionState.Confirmed or UpdateTransactionState.RolledBack)
            return status.State;
        if (!rollbackApplied
            && status.State is (UpdateTransactionState.Applied or UpdateTransactionState.Restarted))
            return status.State;
        if (status.State is UpdateTransactionState.HandoffPrepared or UpdateTransactionState.HandingOff)
        {
            store.ResetJournal();
            await store.WriteStatusAsync(UpdateTransactionState.RolledBack, cancellationToken: cancellationToken);
            return UpdateTransactionState.RolledBack;
        }
        if (status.State is not (UpdateTransactionState.Applying
            or UpdateTransactionState.RollingBack
            or UpdateTransactionState.Failed
            or UpdateTransactionState.Applied
            or UpdateTransactionState.Restarted))
            throw new InvalidOperationException($"事务状态 {status.State} 不需要恢复。");

        await store.WriteStatusAsync(UpdateTransactionState.RollingBack, cancellationToken: cancellationToken);
        try
        {
            await RollbackAsync(plan, store, cancellationToken);
            await store.WriteStatusAsync(UpdateTransactionState.RolledBack, cancellationToken: cancellationToken);
            return UpdateTransactionState.RolledBack;
        }
        catch (Exception exception)
        {
            await store.WriteStatusAsync(UpdateTransactionState.Failed, exception.Message, CancellationToken.None);
            throw;
        }
    }

    public void StartRecoveredApplication(ValidatedUpdatePlan plan)
    {
        var restart = plan.Plan.Restart
            ?? throw new InvalidOperationException("更新计划没有应用重启描述。");
        var previousSha256 = restart.PreviousSha256
            ?? throw new InvalidOperationException("更新计划没有回滚后的应用入口哈希。");
        StartApplication(plan, previousSha256);
    }

    private static async ValueTask PreflightAsync(ValidatedUpdatePlan plan, CancellationToken cancellationToken)
    {
        foreach (var item in plan.Operations)
        {
            var operation = item.Operation;
            if (operation.Kind is UpdateFileOperationKind.Add or UpdateFileOperationKind.Replace)
            {
                await UpdateHash.VerifyFileAsync(
                    item.SourcePath!,
                    operation.SourceSize!.Value,
                    operation.SourceSha256!,
                    cancellationToken);
            }

            var targetExists = File.Exists(item.TargetPath);
            if (operation.Kind == UpdateFileOperationKind.Add && targetExists)
                throw new InvalidDataException($"新增目标已经存在：{operation.TargetPath}");
            if (operation.Kind is UpdateFileOperationKind.Replace or UpdateFileOperationKind.Delete)
            {
                if (!targetExists)
                    throw new FileNotFoundException("替换或删除目标不存在。", item.TargetPath);
                var actual = await UpdateHash.ComputeSha256Async(item.TargetPath, cancellationToken);
                if (!string.Equals(actual, operation.ExistingSha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"现有文件已发生变化：{operation.TargetPath}");
            }
        }

        await UpdateHash.VerifyFileAsync(
            plan.InstalledManifestSourcePath,
            plan.Plan.InstalledManifestSize,
            plan.Plan.InstalledManifestSha256,
            cancellationToken);
        await VerifyUpdaterAsync(plan.Plan.TargetUpdater, cancellationToken);
    }

    private static async ValueTask BackupExistingFilesAsync(
        ValidatedUpdatePlan plan,
        CancellationToken cancellationToken)
    {
        foreach (var item in plan.Operations.Where(item => item.Operation.Kind is UpdateFileOperationKind.Replace or UpdateFileOperationKind.Delete))
            await CopyVerifiedAsync(item.TargetPath, item.BackupPath, item.Operation.ExistingSha256!, cancellationToken);
        if (File.Exists(plan.InstalledManifestTargetPath))
        {
            var hash = await UpdateHash.ComputeSha256Async(plan.InstalledManifestTargetPath, cancellationToken);
            await CopyVerifiedAsync(plan.InstalledManifestTargetPath, plan.InstalledManifestBackupPath, hash, cancellationToken);
        }
    }

    private static async ValueTask ApplyOperationAsync(
        ValidatedUpdatePlan plan,
        UpdateTransactionStore store,
        ValidatedUpdateOperation item,
        int sequence,
        CancellationToken cancellationToken)
    {
        var existedBefore = item.Operation.Kind != UpdateFileOperationKind.Add;
        int? originalUnixMode = existedBefore && !OperatingSystem.IsWindows()
            ? (int)File.GetUnixFileMode(item.TargetPath)
            : null;
        var entry = new UpdateJournalEntry
        {
            Sequence = sequence,
            Phase = UpdateJournalPhase.Prepared,
            Kind = item.Operation.Kind,
            TargetPath = item.Operation.TargetPath,
            BackupPath = existedBefore ? Path.GetRelativePath(plan.BackupDirectory, item.BackupPath).Replace('\\', '/') : null,
            ExistedBefore = existedBefore,
            OriginalUnixMode = originalUnixMode,
            Timestamp = DateTimeOffset.UtcNow,
        };
        await store.AppendJournalAsync(entry, cancellationToken);

        if (item.Operation.Kind == UpdateFileOperationKind.Delete)
        {
            File.Delete(item.TargetPath);
        }
        else
        {
            await ReplaceFromSourceAsync(
                item.SourcePath!,
                item.TargetPath,
                item.Operation.SourceSize!.Value,
                item.Operation.SourceSha256!,
                item.Operation.Executable,
                cancellationToken);
        }
        await store.AppendJournalAsync(entry with
        {
            Phase = UpdateJournalPhase.Completed,
            Timestamp = DateTimeOffset.UtcNow,
        }, cancellationToken);
    }

    private static async ValueTask ApplyInstalledManifestAsync(
        ValidatedUpdatePlan plan,
        UpdateTransactionStore store,
        int sequence,
        CancellationToken cancellationToken)
    {
        var existedBefore = File.Exists(plan.InstalledManifestTargetPath);
        var entry = new UpdateJournalEntry
        {
            Sequence = sequence,
            Phase = UpdateJournalPhase.Prepared,
            Kind = existedBefore ? UpdateFileOperationKind.Replace : UpdateFileOperationKind.Add,
            TargetPath = ".update/installed-manifest.json",
            BackupPath = existedBefore ? ".update/installed-manifest.json" : null,
            ExistedBefore = existedBefore,
            Timestamp = DateTimeOffset.UtcNow,
        };
        await store.AppendJournalAsync(entry, cancellationToken);
        await ReplaceFromSourceAsync(
            plan.InstalledManifestSourcePath,
            plan.InstalledManifestTargetPath,
            plan.Plan.InstalledManifestSize,
            plan.Plan.InstalledManifestSha256,
            executable: false,
            cancellationToken);
        await store.AppendJournalAsync(entry with
        {
            Phase = UpdateJournalPhase.Completed,
            Timestamp = DateTimeOffset.UtcNow,
        }, cancellationToken);
    }

    private static async ValueTask RollbackAsync(
        ValidatedUpdatePlan plan,
        UpdateTransactionStore store,
        CancellationToken cancellationToken)
    {
        var prepared = (await store.ReadJournalAsync(cancellationToken))
            .Where(entry => entry.Phase == UpdateJournalPhase.Prepared)
            .GroupBy(entry => entry.Sequence)
            .Select(group => group.First())
            .OrderByDescending(entry => entry.Sequence)
            .ToArray();
        foreach (var entry in prepared)
        {
            var targetPath = UpdatePathPolicy.ResolveInside(plan.InstallDirectory, entry.TargetPath, nameof(entry.TargetPath));
            if (!entry.ExistedBefore)
            {
                if (File.Exists(targetPath))
                    File.Delete(targetPath);
                continue;
            }
            if (entry.BackupPath is null)
                throw new InvalidDataException($"回滚记录缺少备份路径：{entry.TargetPath}");
            var backupPath = UpdatePathPolicy.ResolveInside(plan.BackupDirectory, entry.BackupPath, nameof(entry.BackupPath));
            if (!File.Exists(backupPath))
                throw new FileNotFoundException("回滚备份不存在。", backupPath);
            var expectedHash = await UpdateHash.ComputeSha256Async(backupPath, cancellationToken);
            if (File.Exists(targetPath))
            {
                var currentHash = await UpdateHash.ComputeSha256Async(targetPath, cancellationToken);
                if (string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
                {
                    if (entry.OriginalUnixMode is { } unchangedUnixMode && !OperatingSystem.IsWindows())
                        File.SetUnixFileMode(targetPath, (UnixFileMode)unchangedUnixMode);
                    continue;
                }
            }
            await ReplaceFromSourceAsync(
                backupPath,
                targetPath,
                new FileInfo(backupPath).Length,
                expectedHash,
                executable: false,
                cancellationToken);
            if (entry.OriginalUnixMode is { } unixMode && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(targetPath, (UnixFileMode)unixMode);
        }
    }

    private static async ValueTask CopyVerifiedAsync(
        string source,
        string destination,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        await UpdateHash.VerifyFileAsync(destination, new FileInfo(source).Length, expectedSha256, cancellationToken);
    }

    private static async ValueTask ReplaceFromSourceAsync(
        string source,
        string target,
        long expectedSize,
        string expectedSha256,
        bool executable,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.GetDirectoryName(target)
            ?? throw new InvalidDataException("更新目标没有父目录。");
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = Path.Combine(targetDirectory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.update");
        try
        {
            File.Copy(source, temporaryPath, overwrite: false);
            await UpdateHash.VerifyFileAsync(temporaryPath, expectedSize, expectedSha256, cancellationToken);
            if (executable && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            try
            {
                File.Move(temporaryPath, target, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"替换更新文件失败：{target}。文件可能被其他进程占用，请先关闭相关程序后重试。",
                    exception);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async ValueTask VerifyUpdaterAsync(
        UpdateUpdaterDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(descriptor.Path);
        if (!info.Exists)
            throw new FileNotFoundException("目标更新器不存在。", descriptor.Path);
        await UpdateHash.VerifyFileAsync(
            descriptor.Path,
            info.Length,
            descriptor.Sha256,
            cancellationToken);
    }

    private static void StartApplication(ValidatedUpdatePlan plan, string expectedSha256)
    {
        var restart = plan.Plan.Restart!;
        var executable = UpdatePathPolicy.ResolveInside(
            plan.InstallDirectory,
            restart.ExecutablePath,
            nameof(restart.ExecutablePath));
        var actualHash = UpdateHash.ComputeSha256Async(executable).AsTask().GetAwaiter().GetResult();
        if (!string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("重启入口哈希不匹配。");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = plan.InstallDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in restart.Arguments)
            startInfo.ArgumentList.Add(argument);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动更新后的应用程序。");
    }
}
