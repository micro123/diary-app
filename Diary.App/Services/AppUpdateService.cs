using Diary.Core.Data.AppConfig;
using Diary.Update;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Services;

[DiAutoRegister(singleton: true)]
public sealed class AppUpdateService(UpdateChecker checker, ILogger<AppUpdateService> logger)
{
    private readonly SemaphoreSlim _checkLock = new(1, 1);

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
            var channel = config.Channel is "stable" or "preview" ? config.Channel : "preview";
            var flavor = ResolveFlavor(config.Flavor ?? "Auto", rid);
            var request = new UpdateCheckRequest(serverUri, channel, rid, flavor, AppInfo.AppSequence);
            logger.LogInformation(
                "检查应用更新：Server={Server}, Channel={Channel}, Rid={Rid}, Flavor={Flavor}, Sequence={Sequence}",
                serverUri,
                request.Channel,
                request.Rid,
                request.Flavor,
                request.CurrentSequence);
            var result = await checker.CheckAsync(request, cancellationToken);
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
}
