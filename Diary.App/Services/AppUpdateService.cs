using Diary.Core.Data.AppConfig;
using Diary.Update;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Services;

[DiAutoRegister(singleton: true)]
public sealed class AppUpdateService(
    UpdateChecker checker,
    UpdatePreparationService preparationService,
    UpdateStartupManager startupManager,
    ILogger<AppUpdateService> logger)
{
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly SemaphoreSlim _prepareLock = new(1, 1);

    public async ValueTask<UpdateCheckResult> CheckAsync(
        UpdateConfig config,
        CancellationToken cancellationToken = default)
    {
        if (!await _checkLock.WaitAsync(0, cancellationToken))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.TemporarilyUnavailable,
                Error: "已有更新检查正在进行。请稍后重试。");
        }
        try
        {
            if (string.IsNullOrWhiteSpace(config.ServerUrl)
                || !Uri.TryCreate(config.ServerUrl.Trim(), UriKind.Absolute, out var serverUri)
                || serverUri.Scheme is not ("http" or "https"))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.InvalidResponse,
                    Error: "更新服务器地址必须是 HTTP 或 HTTPS 绝对地址。");
            }
            var rid = UpdateProtocol.CurrentRid;
            if (rid is not ("win-x64" or "linux-x64"))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.InvalidResponse,
                    Error: $"当前平台暂不支持应用更新：{rid}");
            }
            var channel = config.Channel is "stable" or "preview" or "local" ? config.Channel : "preview";
            var flavor = ResolveFlavor(config.Flavor ?? "Auto", rid);
            var currentSequence = ResolveCurrentSequence(channel, AppInfo.AppBuildChannel, AppInfo.AppSequence);
            var request = new UpdateCheckRequest(serverUri, channel, rid, flavor, currentSequence);
            logger.LogInformation(
                "检查应用更新：Server={Server}, Channel={Channel}, Rid={Rid}, Flavor={Flavor}, Sequence={Sequence}",
                serverUri,
                request.Channel,
                request.Rid,
                request.Flavor,
                request.CurrentSequence);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            UpdateCheckResult result;
            try
            {
                result = await checker.CheckAsync(request, timeout.Token);
            }
            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                result = new UpdateCheckResult(
                    UpdateCheckStatus.TemporarilyUnavailable,
                    Error: "连接更新服务器超时。");
            }
            logger.LogInformation(
                "应用更新检查完成：Status={Status}, TargetSequence={TargetSequence}",
                result.Status,
                result.Envelope?.Manifest.Sequence);
            return result;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public async ValueTask<PreparedUpdate> PrepareAsync(
        UpdateCheckResult result,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (result.Status != UpdateCheckStatus.UpdateAvailable
            || result.Envelope is null
            || result.FullPackageUri is null)
        {
            throw new ArgumentException("更新检查结果不包含可下载的完整包。", nameof(result));
        }
        if (!await _prepareLock.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("已有更新正在下载或准备。请稍后重试。");
        try
        {
            var restartArguments = App.StartupOptions.CoreOnly
                ? new[] { AppStartupOptions.CoreOnlyArgument }
                : [];
            var request = new UpdatePreparationRequest
            {
                PackageUri = result.FullPackageUri,
                Envelope = result.Envelope,
                CurrentVersion = AppInfo.AppVersionString,
                InstallDirectory = FsTools.GetBinaryDirectory(),
                UpdatesRootDirectory = Path.Combine(FsTools.GetApplicationDataDirectory(), "updates"),
                RestartArguments = restartArguments,
            };
            logger.LogInformation(
                "开始下载并准备应用更新：TargetVersion={TargetVersion}, PackageSize={PackageSize}",
                result.Envelope.Manifest.VersionId,
                result.Envelope.FullPackage.Size);
            var prepared = await preparationService.PrepareAsync(request, progress, cancellationToken);
            logger.LogInformation(
                "应用更新准备完成：TransactionId={TransactionId}, Add={Add}, Replace={Replace}, Delete={Delete}, Conflicts={Conflicts}",
                prepared.TransactionId,
                prepared.AddCount,
                prepared.ReplaceCount,
                prepared.DeleteCount,
                prepared.PreservedConflicts.Count);
            return prepared;
        }
        finally
        {
            _prepareLock.Release();
        }
    }

    public void StartPreparedUpdate(PreparedUpdate prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        _ = UpdateProcessServices.StartApply(
            prepared.BootstrapUpdaterPath,
            prepared.PlanPath,
            Environment.ProcessId);
        logger.LogInformation("已启动应用更新引导程序：TransactionId={TransactionId}", prepared.TransactionId);
    }

    public ValueTask ConfirmStartupAsync(
        string planPath,
        CancellationToken cancellationToken = default)
        => startupManager.ConfirmAsync(planPath, AppInfo.AppSequence, cancellationToken);

    public ValueTask<bool> HandleRolledBackStartupAsync(
        string planPath,
        bool startupSucceeded,
        CancellationToken cancellationToken = default)
        => startupManager.HandleRolledBackStartupAsync(planPath, startupSucceeded, cancellationToken);

    public ValueTask StartRollbackAsync(
        string planPath,
        CancellationToken cancellationToken = default)
        => startupManager.StartRollbackAsync(planPath, Environment.ProcessId, cancellationToken);

    private static string ResolveFlavor(string configuredFlavor, string rid)
    {
        if (rid == "linux-x64")
            return "standard";
        if (configuredFlavor == "Auto")
            return Directory.Exists(Path.Combine(FsTools.GetBinaryDirectory(), "python"))
                ? "python313"
                : "standard";
        return configuredFlavor is "standard" or "python313"
            ? configuredFlavor
            : "standard";
    }

    internal static long ResolveCurrentSequence(string targetChannel, string buildChannel, long appSequence) =>
        string.Equals(buildChannel, "local", StringComparison.Ordinal)
        && !string.Equals(targetChannel, "local", StringComparison.Ordinal)
            ? 0
            : appSequence;
}
