namespace Diary.Update;

public sealed record ValidatedUpdateOperation(
    UpdateFileOperation Operation,
    string TargetPath,
    string? SourcePath,
    string BackupPath);

public sealed record ValidatedUpdatePlan(
    UpdateTransactionPlan Plan,
    string InstallDirectory,
    string UpdatesRootDirectory,
    string TransactionDirectory,
    string StagingDirectory,
    string BackupDirectory,
    string InstalledManifestSourcePath,
    string InstalledManifestTargetPath,
    string InstalledManifestBackupPath,
    IReadOnlyList<ValidatedUpdateOperation> Operations);

public static class UpdatePlanValidator
{
    public static ValidatedUpdatePlan Validate(UpdateTransactionPlan plan, string? planPath = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion != UpdateProtocol.PlanSchemaVersion)
            throw new InvalidDataException($"不支持的更新事务版本：{plan.SchemaVersion}");
        if (!Guid.TryParse(plan.TransactionId, out _))
            throw new InvalidDataException("transactionId 必须是 GUID。");
        RequireSecret(plan.TransactionToken, nameof(plan.TransactionToken));
        RequireSecret(plan.HandoffToken, nameof(plan.HandoffToken));
        if (string.IsNullOrWhiteSpace(plan.CurrentVersion) || string.IsNullOrWhiteSpace(plan.TargetVersion))
            throw new InvalidDataException("当前版本和目标版本不能为空。");
        if (plan.Rid is not ("win-x64" or "linux-x64"))
            throw new InvalidDataException($"不支持的 RID：{plan.Rid}");
        if (plan.MinUpdaterVersion <= 0 || plan.WaitForExitTimeoutSeconds is <= 0 or > 600)
            throw new InvalidDataException("更新器版本或退出等待时间非法。");

        var install = UpdatePathPolicy.NormalizeAbsolute(plan.InstallDirectory, nameof(plan.InstallDirectory));
        var updatesRoot = UpdatePathPolicy.NormalizeAbsolute(plan.UpdatesRootDirectory, nameof(plan.UpdatesRootDirectory));
        var transaction = UpdatePathPolicy.NormalizeAbsolute(plan.TransactionDirectory, nameof(plan.TransactionDirectory));
        var staging = UpdatePathPolicy.NormalizeAbsolute(plan.StagingDirectory, nameof(plan.StagingDirectory));
        var backup = UpdatePathPolicy.NormalizeAbsolute(plan.BackupDirectory, nameof(plan.BackupDirectory));
        if (UpdatePathPolicy.Overlaps(install, updatesRoot))
            throw new InvalidDataException("安装目录和更新数据目录不能重叠。");
        UpdatePathPolicy.EnsureInside(updatesRoot, transaction, nameof(plan.TransactionDirectory));
        UpdatePathPolicy.EnsureInside(updatesRoot, staging, nameof(plan.StagingDirectory));
        UpdatePathPolicy.EnsureInside(updatesRoot, backup, nameof(plan.BackupDirectory));
        if (planPath is not null)
            UpdatePathPolicy.EnsureInside(updatesRoot, Path.GetFullPath(planPath), nameof(planPath));
        if (!Directory.Exists(install) || !Directory.Exists(staging))
            throw new DirectoryNotFoundException("安装目录或暂存目录不存在。");

        ValidateUpdater(plan.BootstrapUpdater, updatesRoot, plan.Rid, nameof(plan.BootstrapUpdater));
        var targetUpdaterPath = ValidateUpdater(plan.TargetUpdater, staging, plan.Rid, nameof(plan.TargetUpdater));
        if (plan.TargetUpdater.ProtocolVersion < plan.MinUpdaterVersion)
            throw new InvalidDataException("目标更新器协议版本低于事务要求。");

        var pathComparison = plan.Rid == "win-x64" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var comparer = plan.Rid == "win-x64" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var targetPaths = new HashSet<string>(comparer);
        var operations = new List<ValidatedUpdateOperation>(plan.Operations.Count);
        foreach (var operation in plan.Operations)
        {
            var relativeTarget = UpdatePathPolicy.NormalizeRelative(operation.TargetPath, nameof(operation.TargetPath));
            if (relativeTarget.Equals(".update", pathComparison)
                || relativeTarget.StartsWith(".update/", pathComparison))
                throw new InvalidDataException("更新计划不能写入更新器保留的 .update 目录。");
            if (!targetPaths.Add(relativeTarget))
                throw new InvalidDataException($"更新计划包含重复目标路径：{relativeTarget}");
            var targetPath = UpdatePathPolicy.ResolveInside(install, relativeTarget, nameof(operation.TargetPath));
            var backupPath = UpdatePathPolicy.ResolveInside(backup, relativeTarget, nameof(operation.TargetPath));
            UpdatePathPolicy.RejectExistingLinks(install, targetPath, nameof(operation.TargetPath));

            string? sourcePath = null;
            if (operation.Kind is UpdateFileOperationKind.Add or UpdateFileOperationKind.Replace)
            {
                if (operation.SourceSize is null or < 0 || !UpdateHash.IsSha256(operation.SourceSha256))
                    throw new InvalidDataException($"更新源文件描述无效：{relativeTarget}");
                sourcePath = UpdatePathPolicy.ResolveInside(
                    staging,
                    operation.SourcePath ?? string.Empty,
                    nameof(operation.SourcePath));
                UpdatePathPolicy.RejectExistingLinks(staging, sourcePath, nameof(operation.SourcePath));
            }
            else if (operation.SourcePath is not null || operation.SourceSize is not null || operation.SourceSha256 is not null)
            {
                throw new InvalidDataException($"删除操作不能包含源文件：{relativeTarget}");
            }

            if (operation.Kind is UpdateFileOperationKind.Replace or UpdateFileOperationKind.Delete)
            {
                if (!UpdateHash.IsSha256(operation.ExistingSha256))
                    throw new InvalidDataException($"替换或删除操作缺少原文件哈希：{relativeTarget}");
            }
            else if (operation.ExistingSha256 is not null)
            {
                throw new InvalidDataException($"新增操作不能包含原文件哈希：{relativeTarget}");
            }

            operations.Add(new(operation, targetPath, sourcePath, backupPath));
        }

        var updaterTarget = plan.Rid == "win-x64" ? "Diary.Updater.exe" : "Diary.Updater";
        var updaterOperation = operations.SingleOrDefault(item => comparer.Equals(item.Operation.TargetPath, updaterTarget));
        if (updaterOperation is null
            || updaterOperation.Operation.Kind == UpdateFileOperationKind.Delete
            || updaterOperation.SourcePath is null
            || !string.Equals(Path.GetFullPath(updaterOperation.SourcePath), targetUpdaterPath,
                pathComparison)
            || updaterOperation.Operation.SourceSha256 != plan.TargetUpdater.Sha256
            || !updaterOperation.Operation.Executable)
            throw new InvalidDataException("更新计划必须将已验证的目标 Diary.Updater 作为可执行受管理文件安装。");

        var manifestSource = UpdatePathPolicy.ResolveInside(
            staging,
            plan.InstalledManifestSourcePath,
            nameof(plan.InstalledManifestSourcePath));
        if (plan.InstalledManifestSize < 0 || !UpdateHash.IsSha256(plan.InstalledManifestSha256))
            throw new InvalidDataException("安装清单描述无效。");
        var manifestTarget = UpdatePathPolicy.ResolveInside(install, ".update/installed-manifest.json", "installed manifest");
        var manifestBackup = UpdatePathPolicy.ResolveInside(backup, ".update/installed-manifest.json", "installed manifest backup");
        UpdatePathPolicy.RejectExistingLinks(install, manifestTarget, "installed manifest");

        if (plan.Restart is not null)
        {
            _ = UpdatePathPolicy.ResolveInside(install, plan.Restart.ExecutablePath, nameof(plan.Restart.ExecutablePath));
            if (!UpdateHash.IsSha256(plan.Restart.Sha256))
                throw new InvalidDataException("重启入口哈希无效。");
        }

        return new(plan, install, updatesRoot, transaction, staging, backup, manifestSource,
            manifestTarget, manifestBackup, operations);
    }

    private static string ValidateUpdater(UpdateUpdaterDescriptor descriptor, string root, string rid, string fieldName)
    {
        if (descriptor.ProtocolVersion <= 0 || descriptor.Rid != rid || !UpdateHash.IsSha256(descriptor.Sha256))
            throw new InvalidDataException($"{fieldName} 描述无效。");
        var path = UpdatePathPolicy.NormalizeAbsolute(descriptor.Path, $"{fieldName}.Path");
        UpdatePathPolicy.EnsureInside(root, path, $"{fieldName}.Path");
        UpdatePathPolicy.RejectExistingLinks(root, path, $"{fieldName}.Path");
        return path;
    }

    private static void RequireSecret(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
            throw new InvalidDataException($"{fieldName} 长度不足。");
    }
}
