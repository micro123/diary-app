using System.Text.Json;

namespace Diary.Update;

public sealed class UpdatePreparationService
{
    private readonly IUpdatePackageSource _packageSource;
    private readonly Func<string, CancellationToken, ValueTask<UpdateMachineVersion>> _probeUpdater;

    public UpdatePreparationService(IUpdatePackageSource packageSource)
        : this(packageSource, UpdateProcessServices.ProbeUpdaterAsync)
    {
    }

    internal UpdatePreparationService(
        IUpdatePackageSource packageSource,
        Func<string, CancellationToken, ValueTask<UpdateMachineVersion>> probeUpdater)
    {
        _packageSource = packageSource;
        _probeUpdater = probeUpdater;
    }

    public async ValueTask<PreparedUpdate> PrepareAsync(
        UpdatePreparationRequest request,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var manifest = request.Envelope.Manifest;
        UpdateManifestValidator.Validate(
            request.Envelope,
            manifest.Channel,
            manifest.Rid,
            manifest.Flavor);
        if (string.IsNullOrWhiteSpace(request.CurrentVersion))
            throw new ArgumentException("当前版本不能为空。", nameof(request));

        var installDirectory = UpdatePathPolicy.NormalizeAbsolute(request.InstallDirectory, nameof(request.InstallDirectory));
        var updatesRoot = UpdatePathPolicy.NormalizeAbsolute(request.UpdatesRootDirectory, nameof(request.UpdatesRootDirectory));
        if (UpdatePathPolicy.Overlaps(installDirectory, updatesRoot))
            throw new InvalidDataException("安装目录和更新数据目录不能重叠。");
        Directory.CreateDirectory(updatesRoot);

        var transactionId = Guid.NewGuid().ToString("N");
        var transactionDirectory = Path.Combine(updatesRoot, "transactions", transactionId);
        var stagingDirectory = Path.Combine(transactionDirectory, "staging");
        var backupDirectory = Path.Combine(transactionDirectory, "backup");
        var packagePath = Path.Combine(transactionDirectory, "package.zip");
        var planPath = Path.Combine(transactionDirectory, "transaction.json");
        var bootstrapDirectory = Path.Combine(updatesRoot, "bootstrap", transactionId);
        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(bootstrapDirectory);

        try
        {
            var installedManifest = await TryLoadInstalledManifestAsync(
                installDirectory,
                manifest,
                cancellationToken);
            var useIncremental = installedManifest is not null
                && installedManifest.Sequence >= manifest.MinIncrementalSequence
                && installedManifest.Sequence <= manifest.Sequence;
            UpdateDownloadMode downloadMode;
            long downloadSize;
            var preservedConflicts = new List<string>();
            var updaterName = manifest.Rid == "win-x64" ? "Diary.Updater.exe" : "Diary.Updater";
            var operations = await BuildOperationsAsync(
                installDirectory,
                manifest,
                updaterName,
                installedManifest,
                preservedConflicts,
                cancellationToken);
            await EnsureMcpAvailableAsync(installDirectory, operations, cancellationToken);

            if (useIncremental)
            {
                downloadSize = await DownloadIncrementalFilesAsync(
                    request.ServerUri,
                    stagingDirectory,
                    manifest,
                    operations,
                    progress,
                    cancellationToken);
                downloadMode = UpdateDownloadMode.Incremental;
            }
            else
            {
                await _packageSource.DownloadPackageAsync(
                    request.PackageUri,
                    packagePath,
                    request.Envelope.FullPackage,
                    progress,
                    cancellationToken);
                await UpdatePackageExtractor.ExtractAndValidateAsync(
                    packagePath,
                    stagingDirectory,
                    manifest,
                    cancellationToken);
                downloadMode = UpdateDownloadMode.FullPackage;
                downloadSize = request.Envelope.FullPackage.Size;
            }

            var installedManifestSourcePath = Path.Combine(stagingDirectory, ".update", "installed-manifest.json");
            await WriteManifestAsync(installedManifestSourcePath, manifest, cancellationToken);
            var installedManifestSize = new FileInfo(installedManifestSourcePath).Length;
            var installedManifestSha256 = await UpdateHash.ComputeSha256Async(
                installedManifestSourcePath,
                cancellationToken);

            var appName = manifest.Rid == "win-x64" ? "Diary.App.exe" : "Diary.App";
            var targetUpdaterFile = RequireFile(manifest, updaterName, "updater");
            var targetAppFile = RequireFile(manifest, appName, "app");
            var installedAppPath = UpdatePathPolicy.ResolveInside(installDirectory, targetAppFile.Path, targetAppFile.Path);
            var previousAppSha256 = File.Exists(installedAppPath)
                ? await ComputeInstalledFileSha256Async(
                    installedAppPath,
                    targetAppFile.Path,
                    cancellationToken)
                : null;
            var targetUpdaterPath = UpdatePathPolicy.ResolveInside(
                stagingDirectory,
                targetUpdaterFile.Path,
                targetUpdaterFile.Path);
            var targetVersion = await ProbeTargetUpdaterAsync(
                targetUpdaterPath,
                targetUpdaterFile.Path,
                cancellationToken);
            if (targetVersion.Rid != manifest.Rid || targetVersion.ProtocolVersion < manifest.MinUpdaterVersion)
                throw new InvalidDataException("目标更新器的 RID 或协议版本不满足目标清单。");

            var bootstrapUpdaterPath = Path.Combine(bootstrapDirectory, updaterName);
            await CopyBootstrapUpdaterAsync(
                targetUpdaterPath,
                bootstrapUpdaterPath,
                targetUpdaterFile.Path,
                cancellationToken);
            if (targetUpdaterFile.Executable && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(bootstrapUpdaterPath, ExecutableMode);
            try
            {
                await UpdateHash.VerifyFileAsync(
                    bootstrapUpdaterPath,
                    targetUpdaterFile.Size,
                    targetUpdaterFile.Sha256,
                    cancellationToken);
            }
            catch (IOException exception)
            {
                throw CreateFileInUseException("验证更新引导程序", targetUpdaterFile.Path, exception);
            }

            EnsureBackupCapacity(updatesRoot, installDirectory, operations, installedManifestSize);

            var plan = new UpdateTransactionPlan
            {
                TransactionId = transactionId,
                TransactionToken = CreateSecret(),
                HandoffToken = CreateSecret(),
                CurrentVersion = request.CurrentVersion,
                TargetVersion = manifest.VersionId,
                Rid = manifest.Rid,
                InstallDirectory = installDirectory,
                UpdatesRootDirectory = updatesRoot,
                TransactionDirectory = transactionDirectory,
                StagingDirectory = stagingDirectory,
                BackupDirectory = backupDirectory,
                MinUpdaterVersion = manifest.MinUpdaterVersion,
                Operations = operations,
                InstalledManifestSourcePath = Path.GetRelativePath(stagingDirectory, installedManifestSourcePath)
                    .Replace('\\', '/'),
                InstalledManifestSize = installedManifestSize,
                InstalledManifestSha256 = installedManifestSha256,
                BootstrapUpdater = new UpdateUpdaterDescriptor
                {
                    Path = bootstrapUpdaterPath,
                    Sha256 = targetUpdaterFile.Sha256,
                    Rid = manifest.Rid,
                    ProtocolVersion = targetVersion.ProtocolVersion,
                },
                TargetUpdater = new UpdateUpdaterDescriptor
                {
                    Path = targetUpdaterPath,
                    Sha256 = targetUpdaterFile.Sha256,
                    Rid = manifest.Rid,
                    ProtocolVersion = targetVersion.ProtocolVersion,
                },
                Restart = new UpdateRestartOptions
                {
                    ExecutablePath = targetAppFile.Path,
                    Sha256 = targetAppFile.Sha256,
                    PreviousSha256 = previousAppSha256,
                    Arguments = [.. request.RestartArguments, UpdateProtocol.StartupTransactionArgument, planPath],
                },
            };
            await UpdateTransactionStore.WritePlanAsync(planPath, plan, cancellationToken);
            var validated = UpdatePlanValidator.Validate(plan, planPath);
            await new UpdateTransactionStore(validated).WriteStatusAsync(
                UpdateTransactionState.ReadyToApply,
                cancellationToken: cancellationToken);

            return new PreparedUpdate(
                transactionId,
                planPath,
                bootstrapUpdaterPath,
                manifest.VersionId,
                manifest.Sequence,
                downloadMode,
                downloadSize,
                request.Envelope.FullPackage.Size,
                operations.Count(operation => operation.Kind == UpdateFileOperationKind.Add),
                operations.Count(operation => operation.Kind == UpdateFileOperationKind.Replace),
                operations.Count(operation => operation.Kind == UpdateFileOperationKind.Delete),
                preservedConflicts);
        }
        catch
        {
            TryDeleteDirectory(transactionDirectory);
            TryDeleteDirectory(bootstrapDirectory);
            throw;
        }
    }

    private static async ValueTask<List<UpdateFileOperation>> BuildOperationsAsync(
        string installDirectory,
        UpdateManifest manifest,
        string updaterName,
        UpdateManifest? installedManifest,
        List<string> preservedConflicts,
        CancellationToken cancellationToken)
    {
        var operations = new List<UpdateFileOperation>();
        foreach (var file in manifest.Files)
        {
            var targetPath = UpdatePathPolicy.ResolveInside(installDirectory, file.Path, file.Path);
            UpdatePathPolicy.RejectExistingLinks(installDirectory, targetPath, file.Path);
            if (Directory.Exists(targetPath))
                throw new InvalidDataException($"安装目录中的文件目标被目录占用：{file.Path}");
            var info = new FileInfo(targetPath);
            string? existingSha256 = null;
            var unchanged = false;
            if (info.Exists)
            {
                existingSha256 = await ComputeInstalledFileSha256Async(
                    targetPath,
                    file.Path,
                    cancellationToken);
                unchanged = info.Length == file.Size
                    && string.Equals(existingSha256, file.Sha256, StringComparison.Ordinal);
            }
            if (unchanged && !string.Equals(file.Path, updaterName, StringComparison.Ordinal))
                continue;
            operations.Add(new UpdateFileOperation
            {
                Kind = info.Exists ? UpdateFileOperationKind.Replace : UpdateFileOperationKind.Add,
                TargetPath = file.Path,
                SourcePath = file.Path,
                SourceSize = file.Size,
                SourceSha256 = file.Sha256,
                ExistingSha256 = existingSha256,
                Executable = file.Executable,
            });
        }

        if (installedManifest is null)
            return operations;
        var comparer = manifest.Rid == "win-x64" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var targetPaths = manifest.Files.Select(file => file.Path).ToHashSet(comparer);
        foreach (var previous in installedManifest.Files.Where(file => !targetPaths.Contains(file.Path)))
        {
            var targetPath = UpdatePathPolicy.ResolveInside(installDirectory, previous.Path, previous.Path);
            UpdatePathPolicy.RejectExistingLinks(installDirectory, targetPath, previous.Path);
            if (!File.Exists(targetPath))
                continue;
            var actualSha256 = await ComputeInstalledFileSha256Async(
                targetPath,
                previous.Path,
                cancellationToken);
            if (!string.Equals(actualSha256, previous.Sha256, StringComparison.Ordinal))
            {
                preservedConflicts.Add(previous.Path);
                continue;
            }
            operations.Add(new UpdateFileOperation
            {
                Kind = UpdateFileOperationKind.Delete,
                TargetPath = previous.Path,
                ExistingSha256 = actualSha256,
            });
        }
        return operations;
    }

    private async ValueTask<long> DownloadIncrementalFilesAsync(
        Uri serverUri,
        string stagingDirectory,
        UpdateManifest manifest,
        IReadOnlyList<UpdateFileOperation> operations,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var comparer = manifest.Rid == "win-x64" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = manifest.Files.ToDictionary(file => file.Path, comparer);
        var downloads = operations
            .Where(operation => operation.Kind is UpdateFileOperationKind.Add or UpdateFileOperationKind.Replace)
            .Select(operation => files[operation.TargetPath])
            .ToArray();
        var totalBytes = downloads.Sum(file => file.Size);
        long completedBytes = 0;
        progress?.Report(new(0, totalBytes));
        foreach (var file in downloads)
        {
            var targetPath = UpdatePathPolicy.ResolveInside(stagingDirectory, file.Path, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var completedBeforeFile = completedBytes;
            var fileProgress = progress is null
                ? null
                : new InlineProgress<UpdateDownloadProgress>(value =>
                    progress.Report(new(completedBeforeFile + value.BytesReceived, totalBytes)));
            await _packageSource.DownloadContentAsync(
                UpdateUris.Content(serverUri, file.Sha256),
                targetPath,
                file,
                fileProgress,
                cancellationToken);
            if (file.Executable && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(targetPath, ExecutableMode);
            completedBytes += file.Size;
            progress?.Report(new(completedBytes, totalBytes));
        }
        return totalBytes;
    }

    internal static async ValueTask EnsureMcpAvailableAsync(
        string installDirectory,
        IReadOnlyList<UpdateFileOperation> operations,
        CancellationToken cancellationToken,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var operation = operations.FirstOrDefault(item =>
            string.Equals(item.TargetPath, "Diary.Mcp.exe", StringComparison.OrdinalIgnoreCase));
        if (operation is null)
            return;
        var path = UpdatePathPolicy.ResolveInside(installDirectory, operation.TargetPath, operation.TargetPath);
        if (!File.Exists(path))
            return;
        try
        {
            await UpdateFileAccess.ExecuteWithSharingRetryAsync(() =>
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return ValueTask.FromResult(true);
            }, cancellationToken, retryDelays);
        }
        catch (Exception exception) when (UpdateFileAccess.IsSharingViolation(exception))
        {
            throw new IOException(
                "Diary MCP 正被外部 AI 客户端占用，无法安全更新。请先结束当前 MCP 会话或关闭相关 AI 客户端，然后重试更新。",
                exception);
        }
    }

    private static async ValueTask<string> ComputeInstalledFileSha256Async(
        string path,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await UpdateHash.ComputeSha256Async(path, cancellationToken);
        }
        catch (IOException exception)
        {
            throw CreateFileInUseException("读取已安装文件", relativePath, exception);
        }
    }

    private async ValueTask<UpdateMachineVersion> ProbeTargetUpdaterAsync(
        string path,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await UpdateFileAccess.ExecuteWithSharingRetryAsync(
                () => _probeUpdater(path, cancellationToken),
                cancellationToken);
        }
        catch (Exception exception) when (UpdateFileAccess.IsSharingViolation(exception))
        {
            throw CreateFileInUseException("启动目标更新器探针", relativePath, exception);
        }
    }

    private static async ValueTask CopyBootstrapUpdaterAsync(
        string sourcePath,
        string targetPath,
        string relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await UpdateFileAccess.ExecuteWithSharingRetryAsync(
                () =>
                {
                    File.Copy(sourcePath, targetPath, overwrite: false);
                    return ValueTask.FromResult(true);
                },
                cancellationToken);
        }
        catch (IOException exception)
        {
            throw CreateFileInUseException("复制更新引导程序", relativePath, exception);
        }
    }

    private static IOException CreateFileInUseException(
        string operation,
        string relativePath,
        Exception innerException) =>
        new($"{operation}失败：{relativePath}。文件可能被其他进程或安全软件占用，请稍后重试。", innerException);

    private static async ValueTask<UpdateManifest?> TryLoadInstalledManifestAsync(
        string installDirectory,
        UpdateManifest target,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(installDirectory, ".update", "installed-manifest.json");
        if (!File.Exists(path))
            return null;
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var manifest = await JsonSerializer.DeserializeAsync(
                stream,
                UpdateJson.Context.UpdateManifest,
                cancellationToken);
            if (manifest is null)
                return null;
            UpdateManifestValidator.ValidateManifest(
                manifest,
                channel: target.Channel,
                rid: target.Rid,
                flavor: target.Flavor);
            return manifest;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static async ValueTask WriteManifestAsync(
        string path,
        UpdateManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, manifest, UpdateJson.Context.UpdateManifest, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static UpdateManifestFile RequireFile(UpdateManifest manifest, string path, string component) =>
        manifest.Files.SingleOrDefault(file =>
            string.Equals(file.Path, path, StringComparison.Ordinal)
            && string.Equals(file.Component, component, StringComparison.Ordinal))
        ?? throw new InvalidDataException($"目标清单缺少必需文件：{path}");

    private static void EnsureBackupCapacity(
        string updatesRoot,
        string installDirectory,
        IReadOnlyList<UpdateFileOperation> operations,
        long installedManifestSize)
    {
        long backupBytes = installedManifestSize;
        foreach (var operation in operations.Where(operation =>
                     operation.Kind is UpdateFileOperationKind.Replace or UpdateFileOperationKind.Delete))
        {
            var target = UpdatePathPolicy.ResolveInside(installDirectory, operation.TargetPath, operation.TargetPath);
            if (File.Exists(target))
                backupBytes = checked(backupBytes + new FileInfo(target).Length);
        }
        var root = Path.GetPathRoot(updatesRoot);
        if (string.IsNullOrEmpty(root))
            return;
        var drive = new DriveInfo(root);
        const long reserve = 64L * 1024 * 1024;
        if (drive.IsReady && drive.AvailableFreeSpace < backupBytes + reserve)
            throw new IOException("更新备份空间不足，至少需要额外保留 64 MiB 安全余量。");
    }

    private static string CreateSecret() =>
        Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 保留失败现场，由后续启动清理。
        }
    }

    private static readonly UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
