using System.Text.Json;

namespace Diary.Update;

public sealed class UpdateStartupManager
{
    public async ValueTask<UpdateTransactionStatus> ReadStatusAsync(
        string planPath,
        CancellationToken cancellationToken = default)
    {
        var plan = await UpdateTransactionStore.LoadPlanAsync(planPath, cancellationToken);
        var validated = UpdatePlanValidator.Validate(plan, planPath);
        return await new UpdateTransactionStore(validated).ReadStatusAsync(cancellationToken)
            ?? throw new InvalidDataException("更新事务没有启动状态。");
    }

    public async ValueTask<bool> HandleRolledBackStartupAsync(
        string planPath,
        bool startupSucceeded,
        CancellationToken cancellationToken = default)
    {
        var plan = await UpdateTransactionStore.LoadPlanAsync(planPath, cancellationToken);
        var validated = UpdatePlanValidator.Validate(plan, planPath);
        var store = new UpdateTransactionStore(validated);
        var status = await store.ReadStatusAsync(cancellationToken)
            ?? throw new InvalidDataException("更新事务没有启动状态。");
        if (status.State != UpdateTransactionState.RolledBack)
            return false;
        if (!startupSucceeded)
            return true;

        var restart = plan.Restart
            ?? throw new InvalidDataException("回滚事务没有应用重启描述。");
        var previousSha256 = restart.PreviousSha256
            ?? throw new InvalidDataException("回滚事务没有旧应用入口哈希。");
        var executablePath = UpdatePathPolicy.ResolveInside(
            validated.InstallDirectory,
            restart.ExecutablePath,
            nameof(restart.ExecutablePath));
        var actualSha256 = await UpdateHash.ComputeSha256Async(executablePath, cancellationToken);
        if (!string.Equals(actualSha256, previousSha256, StringComparison.Ordinal))
            throw new InvalidDataException("回滚后的应用入口哈希不匹配。");
        Cleanup(validated);
        return true;
    }

    public async ValueTask ConfirmAsync(
        string planPath,
        long currentSequence,
        CancellationToken cancellationToken = default)
    {
        var plan = await UpdateTransactionStore.LoadPlanAsync(planPath, cancellationToken);
        var validated = UpdatePlanValidator.Validate(plan, planPath);
        var store = new UpdateTransactionStore(validated);
        var status = await store.ReadStatusAsync(cancellationToken)
            ?? throw new InvalidDataException("更新事务没有启动状态。");
        if (status.State is not (UpdateTransactionState.Applied or UpdateTransactionState.Restarted))
            throw new InvalidOperationException($"更新事务状态 {status.State} 不能确认成功。");

        var installedManifestPath = Path.Combine(
            validated.InstallDirectory,
            ".update",
            "installed-manifest.json");
        await using var stream = new FileStream(
            installedManifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync(
                stream,
                UpdateJson.Context.UpdateManifest,
                cancellationToken)
            ?? throw new InvalidDataException("已安装更新清单为空。");
        UpdateManifestValidator.ValidateManifest(manifest, rid: plan.Rid);
        if (manifest.Sequence != currentSequence || !string.Equals(manifest.VersionId, plan.TargetVersion, StringComparison.Ordinal))
            throw new InvalidDataException("当前应用版本与更新事务目标不一致。");

        await store.WriteStatusAsync(UpdateTransactionState.Confirmed, cancellationToken: cancellationToken);
        Cleanup(validated);
    }

    public async ValueTask StartRollbackAsync(
        string planPath,
        int waitProcessId,
        CancellationToken cancellationToken = default)
    {
        var plan = await UpdateTransactionStore.LoadPlanAsync(planPath, cancellationToken);
        var validated = UpdatePlanValidator.Validate(plan, planPath);
        var bootstrap = validated.Plan.BootstrapUpdater;
        var info = new FileInfo(bootstrap.Path);
        if (!info.Exists)
            throw new FileNotFoundException("更新引导程序不存在。", bootstrap.Path);
        await UpdateHash.VerifyFileAsync(bootstrap.Path, info.Length, bootstrap.Sha256, cancellationToken);
        var machineVersion = await UpdateProcessServices.ProbeUpdaterAsync(bootstrap.Path, cancellationToken);
        if (machineVersion.Rid != plan.Rid || machineVersion.ProtocolVersion != bootstrap.ProtocolVersion)
            throw new InvalidDataException("更新引导程序身份验证失败。");
        _ = UpdateProcessServices.StartRecovery(bootstrap.Path, planPath, waitProcessId);
    }

    private static void Cleanup(ValidatedUpdatePlan plan)
    {
        var bootstrapDirectory = Path.GetDirectoryName(plan.Plan.BootstrapUpdater.Path);
        if (bootstrapDirectory is not null && !TryDeleteDirectory(bootstrapDirectory))
            return;
        _ = TryDeleteDirectory(plan.TransactionDirectory);
    }

    private static bool TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException && attempt < 4)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
        return false;
    }
}
