using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Services;

public sealed record ScriptShareExportItem(
    string SourcePath,
    string Id,
    string Name,
    ScriptScope Scope,
    ScriptEntryKind EntryKind,
    string Language,
    ScriptFileMetadata? Metadata = null,
    bool BuildSucceeded = true);

public sealed record ScriptShareExistingItem(string Id, string SourcePath);

public sealed record ScriptShareImportPreviewItem(
    string Id,
    string Name,
    ScriptScope Scope,
    ScriptEntryKind EntryKind,
    string Language,
    string OriginalFileName,
    string TargetSourcePath,
    string? ExistingSourcePath,
    bool HasConflict,
    string Status);

public sealed record ScriptSharePackagePreview(
    string PackagePath,
    IReadOnlyList<ScriptShareImportPreviewItem> Items);

public sealed record ScriptShareImportDecision(string Id, bool ReplaceExisting);

public sealed record ScriptShareImportResult(int ImportedCount, int SkippedCount);

[DiAutoRegister(singleton: true)]
public sealed class ScriptSharePackageService(ILogger<ScriptSharePackageService> logger)
{
    public const string FileExtension = ".diaryscripts";
    public const string FormatId = "diary.script.package";
    public const int FormatVersion = 1;
    public const int MaxScriptCount = 200;
    public const long MaxPackageBytes = 50L * 1024 * 1024;
    public const long MaxSourceBytes = 2L * 1024 * 1024;
    public const long MaxMetadataBytes = 1024 * 1024;
    private const long MaxManifestBytes = 1024 * 1024;
    private const int MaxArchiveEntries = 1 + MaxScriptCount * 2;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async ValueTask ExportAsync(
        string packagePath,
        string scriptRoot,
        IReadOnlyCollection<ScriptShareExportItem> scripts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptRoot);
        ArgumentNullException.ThrowIfNull(scripts);
        if (scripts.Count is < 1 or > MaxScriptCount)
            throw new InvalidDataException($"每个共享包必须包含 1 到 {MaxScriptCount} 个脚本。");

        var root = Path.GetFullPath(scriptRoot);
        var duplicateId = scripts.GroupBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
            throw new InvalidDataException($"导出列表包含重复脚本 ID：{duplicateId.Key}。");

        var failedScript = scripts.FirstOrDefault(item => !item.BuildSucceeded);
        if (failedScript is not null)
            throw new InvalidDataException($"脚本 {failedScript.Id} 加载失败，不能导出。请修复脚本后重新加载。");

        var prepared = new List<PreparedExportItem>(scripts.Count);
        var index = 0;
        foreach (var script in scripts.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ScriptCreationPolicy.IsValidId(script.Id) || string.IsNullOrWhiteSpace(script.Name))
                throw new InvalidDataException("脚本 ID 或名称无效。");
            var sourcePath = Path.GetFullPath(script.SourcePath);
            if (!ScriptCreationPolicy.IsInsideDirectory(sourcePath, root) || !File.Exists(sourcePath))
                throw new InvalidDataException($"脚本源码不在脚本目录内或不存在：{script.Id}。");
            var fileName = Path.GetFileName(sourcePath);
            var language = GetLanguage(Path.GetExtension(fileName));
            if (!string.Equals(language, script.Language, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"脚本语言与扩展名不一致：{script.Id}。");

            var sourceBytes = await ReadFileLimitedAsync(sourcePath, MaxSourceBytes, cancellationToken);
            byte[]? metadataBytes = null;
            var metadataPath = sourcePath + ".json";
            if (File.Exists(metadataPath))
            {
                metadataBytes = await ReadFileLimitedAsync(metadataPath, MaxMetadataBytes, cancellationToken);
            }
            else if (script.Metadata is not null && HasPortableMetadata(script.Metadata))
            {
                metadataBytes = JsonSerializer.SerializeToUtf8Bytes(script.Metadata, MetadataJsonOptions);
            }
            if (metadataBytes is not null)
            {
                if (metadataBytes.LongLength > MaxMetadataBytes)
                    throw new InvalidDataException($"脚本 {script.Id} 的 metadata 超过 1 MiB 限制。");
                ValidateMetadata(metadataBytes, script.Id, script.Name, script.Scope, script.EntryKind, script.Language);
            }

            var prefix = $"scripts/{index++:D3}";
            prepared.Add(new PreparedExportItem(
                script,
                fileName,
                $"{prefix}/{fileName}",
                metadataBytes is null ? null : $"{prefix}/metadata.json",
                sourceBytes,
                metadataBytes));
        }

        var manifest = new PackageManifest(
            FormatId,
            FormatVersion,
            DateTimeOffset.UtcNow,
            prepared.Select(item => new PackageScriptItem(
                item.Script.Id,
                item.Script.Name,
                item.Script.Scope,
                item.Script.EntryKind,
                item.Script.Language,
                item.FileName,
                item.SourceEntryPath,
                item.MetadataEntryPath,
                ComputeSha256(item.SourceBytes),
                item.MetadataBytes is null ? null : ComputeSha256(item.MetadataBytes))).ToArray());

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.LongLength > MaxManifestBytes)
            throw new InvalidDataException("脚本共享包 manifest 超过 1 MiB 限制。");
        var totalContentBytes = manifestBytes.LongLength + prepared.Sum(item =>
            item.SourceBytes.LongLength + (item.MetadataBytes?.LongLength ?? 0));
        if (totalContentBytes > MaxPackageBytes)
            throw new InvalidDataException("脚本共享包解压内容超过 50 MiB 限制。");

        var outputPath = Path.GetFullPath(packagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("共享包输出目录无效。"));
        var tempPath = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteEntryAsync(
                    archive.CreateEntry("manifest.json", CompressionLevel.Optimal),
                    manifestBytes,
                    cancellationToken);
                foreach (var item in prepared)
                {
                    await WriteEntryAsync(
                        archive.CreateEntry(item.SourceEntryPath, CompressionLevel.Optimal),
                        item.SourceBytes,
                        cancellationToken);
                    if (item.MetadataEntryPath is not null && item.MetadataBytes is not null)
                    {
                        await WriteEntryAsync(
                            archive.CreateEntry(item.MetadataEntryPath, CompressionLevel.Optimal),
                            item.MetadataBytes,
                            cancellationToken);
                    }
                }
            }

            if (new FileInfo(tempPath).Length > MaxPackageBytes)
                throw new InvalidDataException("生成的脚本共享包超过 50 MiB 限制。");
            File.Move(tempPath, outputPath, true);
            logger.LogInformation("已导出脚本共享包：{PackagePath}，脚本数 {ScriptCount}", outputPath, scripts.Count);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public async ValueTask<ScriptSharePackagePreview> InspectAsync(
        string packagePath,
        string scriptRoot,
        IReadOnlyCollection<ScriptShareExistingItem> existingScripts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptRoot);
        ArgumentNullException.ThrowIfNull(existingScripts);
        var package = Path.GetFullPath(packagePath);
        if (!File.Exists(package) || new FileInfo(package).Length > MaxPackageBytes)
            throw new InvalidDataException("脚本共享包不存在或超过 50 MiB 限制。");

        var root = Path.GetFullPath(scriptRoot);
        var existingById = existingScripts
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().SourcePath, StringComparer.Ordinal);
        await using var stream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var content = await ReadAndValidatePackageAsync(archive, cancellationToken);
        var items = new List<ScriptShareImportPreviewItem>(content.Manifest.Scripts.Count);
        var targetPaths = new HashSet<string>(GetPathComparer());
        foreach (var item in content.Manifest.Scripts)
        {
            var scopeDirectory = Path.Combine(root, item.Scope == ScriptScope.Editor ? "editor" : "application");
            var targetPath = Path.GetFullPath(Path.Combine(scopeDirectory, item.OriginalFileName));
            existingById.TryGetValue(item.Id, out var existingByIdPath);
            if (existingByIdPath is not null)
            {
                existingByIdPath = Path.GetFullPath(existingByIdPath);
                if (!ScriptCreationPolicy.IsInsideDirectory(existingByIdPath, scopeDirectory)
                    || !string.Equals(
                        Path.GetExtension(existingByIdPath),
                        Path.GetExtension(item.OriginalFileName),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"同 ID 脚本 {item.Id} 的语言或作用域不同，请先删除旧脚本再导入。");
                }
                targetPath = existingByIdPath;
            }
            if (!ScriptCreationPolicy.IsInsideDirectory(targetPath, root) || !targetPaths.Add(targetPath))
                throw new InvalidDataException("共享包包含重复或越界的目标脚本路径。");

            var existingSourcePath = existingByIdPath;
            if (existingSourcePath is null && File.Exists(targetPath))
                existingSourcePath = targetPath;
            var hasConflict = existingSourcePath is not null || File.Exists(targetPath);
            var status = existingByIdPath is not null
                ? "存在相同脚本 ID；勾选后将覆盖现有源码和运行配置。"
                : File.Exists(targetPath)
                    ? "目标文件已存在；勾选后将覆盖该文件。"
                    : "可以导入";
            items.Add(new ScriptShareImportPreviewItem(
                item.Id,
                item.Name,
                item.Scope,
                item.EntryKind,
                item.Language,
                item.OriginalFileName,
                targetPath,
                existingSourcePath,
                hasConflict,
                status));
        }

        return new ScriptSharePackagePreview(package, items);
    }

    public async ValueTask<ScriptShareImportResult> ImportAsync(
        ScriptSharePackagePreview preview,
        string scriptRoot,
        IReadOnlyCollection<ScriptShareImportDecision> decisions,
        IReadOnlyCollection<ScriptShareExistingItem> existingScripts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(decisions);
        var refreshed = await InspectAsync(preview.PackagePath, scriptRoot, existingScripts, cancellationToken);
        var decisionMap = decisions.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var selected = refreshed.Items
            .Where(item => decisionMap.ContainsKey(item.Id))
            .ToArray();
        if (selected.Length == 0)
            return new ScriptShareImportResult(0, refreshed.Items.Count);
        foreach (var item in selected)
        {
            if (item.HasConflict && !decisionMap[item.Id].ReplaceExisting)
                throw new InvalidOperationException($"脚本 {item.Id} 存在冲突，但未授权覆盖。");
        }

        await using var stream = new FileStream(refreshed.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var content = await ReadAndValidatePackageAsync(archive, cancellationToken);
        var packageItems = content.Manifest.Scripts.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var root = Path.GetFullPath(scriptRoot);
        Directory.CreateDirectory(root);
        var backupDirectory = Path.Combine(root, $".import-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupDirectory);
        var backups = new List<(string Original, string Backup)>();
        var createdFiles = new List<string>();
        var preserveBackupDirectory = false;
        try
        {
            foreach (var previewItem in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var packageItem = packageItems[previewItem.Id];
                var affectedPaths = new[]
                {
                    previewItem.TargetSourcePath,
                    previewItem.TargetSourcePath + ".json",
                    previewItem.ExistingSourcePath,
                    previewItem.ExistingSourcePath is null ? null : previewItem.ExistingSourcePath + ".json",
                }.Where(path => path is not null)
                    .Cast<string>()
                    .Distinct(GetPathComparer());
                foreach (var affectedPath in affectedPaths)
                {
                    EnsureInsideRoot(affectedPath, root);
                    if (!File.Exists(affectedPath))
                        continue;
                    var backupPath = Path.Combine(backupDirectory, $"{backups.Count:D4}.bak");
                    File.Move(affectedPath, backupPath);
                    backups.Add((affectedPath, backupPath));
                }

                Directory.CreateDirectory(Path.GetDirectoryName(previewItem.TargetSourcePath)!);
                var sourceBytes = content.EntryBytes[packageItem.SourcePath];
                await WriteImportedFileAsync(previewItem.TargetSourcePath, sourceBytes, createdFiles, cancellationToken);
                if (packageItem.MetadataPath is not null)
                {
                    await WriteImportedFileAsync(
                        previewItem.TargetSourcePath + ".json",
                        content.EntryBytes[packageItem.MetadataPath],
                        createdFiles,
                        cancellationToken);
                }
            }

            logger.LogInformation(
                "已导入脚本共享包：{PackagePath}，脚本数 {ScriptCount}",
                refreshed.PackagePath,
                selected.Length);
            return new ScriptShareImportResult(selected.Length, refreshed.Items.Count - selected.Length);
        }
        catch (Exception importException)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var path in createdFiles.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(exception);
                }
            }
            foreach (var (original, backup) in backups.AsEnumerable().Reverse())
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    if (File.Exists(backup))
                        File.Move(backup, original, true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    rollbackErrors.Add(exception);
                }
            }
            if (rollbackErrors.Count > 0)
            {
                preserveBackupDirectory = true;
                throw new IOException(
                    $"脚本导入失败且自动恢复未完成，剩余备份保留在：{backupDirectory}",
                    new AggregateException([importException, .. rollbackErrors]));
            }
            throw;
        }
        finally
        {
            if (!preserveBackupDirectory && Directory.Exists(backupDirectory))
            {
                try
                {
                    Directory.Delete(backupDirectory, true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "清理脚本导入备份目录失败：{BackupDirectory}", backupDirectory);
                }
            }
        }
    }

    private static async ValueTask<ValidatedPackage> ReadAndValidatePackageAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count is < 2 or > MaxArchiveEntries)
            throw new InvalidDataException("脚本共享包的文件数量无效。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            if (!IsSafeArchivePath(entry.FullName) || !entries.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("脚本共享包包含非法或重复路径。");
            totalLength = checked(totalLength + entry.Length);
            if (totalLength > MaxPackageBytes)
                throw new InvalidDataException("脚本共享包解压内容超过 50 MiB 限制。");
        }

        if (!entries.TryGetValue("manifest.json", out var manifestEntry))
            throw new InvalidDataException("脚本共享包缺少 manifest.json。");
        var manifestBytes = await ReadEntryLimitedAsync(manifestEntry, MaxManifestBytes, cancellationToken);
        var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidDataException("脚本共享包 manifest 无效。");
        if (!string.Equals(manifest.Format, FormatId, StringComparison.Ordinal)
            || manifest.FormatVersion != FormatVersion
            || manifest.Scripts is null
            || manifest.Scripts.Count is < 1 or > MaxScriptCount)
        {
            throw new InvalidDataException("不支持的脚本共享包格式或版本。");
        }
        if (manifest.Scripts.Any(item => item is null))
            throw new InvalidDataException("脚本共享包包含空的脚本描述。");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var referencedEntries = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        var entryBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in manifest.Scripts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ids.Add(item.Id)
                || !ScriptCreationPolicy.IsValidId(item.Id)
                || string.IsNullOrWhiteSpace(item.Name)
                || !Enum.IsDefined(item.Scope)
                || !Enum.IsDefined(item.EntryKind)
                || !ScriptEntryKindResolver.IsCompatible(item.EntryKind, item.Scope)
                || !IsSafeFileName(item.OriginalFileName)
                || !string.Equals(GetLanguage(Path.GetExtension(item.OriginalFileName)), item.Language, StringComparison.OrdinalIgnoreCase)
                || !IsSafeArchivePath(item.SourcePath)
                || !item.SourcePath.StartsWith("scripts/", StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(item.SourcePath), item.OriginalFileName, StringComparison.Ordinal)
                || (item.MetadataPath is null) != (item.MetadataSha256 is null)
                || item.MetadataPath is not null
                    && (!IsSafeArchivePath(item.MetadataPath)
                        || !item.MetadataPath.StartsWith("scripts/", StringComparison.Ordinal)
                        || !string.Equals(Path.GetFileName(item.MetadataPath), "metadata.json", StringComparison.Ordinal)
                        || !string.Equals(
                            GetArchiveDirectory(item.MetadataPath),
                            GetArchiveDirectory(item.SourcePath),
                            StringComparison.Ordinal)))
            {
                throw new InvalidDataException("脚本共享包包含无效脚本描述。");
            }
            if (!referencedEntries.Add(item.SourcePath)
                || !entries.TryGetValue(item.SourcePath, out var sourceEntry))
                throw new InvalidDataException($"脚本 {item.Id} 的源码条目缺失或重复。");
            var sourceBytes = await ReadEntryLimitedAsync(sourceEntry, MaxSourceBytes, cancellationToken);
            if (!string.Equals(ComputeSha256(sourceBytes), item.SourceSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"脚本 {item.Id} 的源码校验失败。");
            entryBytes[item.SourcePath] = sourceBytes;

            if (item.MetadataPath is null)
                continue;
            if (!referencedEntries.Add(item.MetadataPath)
                || !entries.TryGetValue(item.MetadataPath, out var metadataEntry))
                throw new InvalidDataException($"脚本 {item.Id} 的 metadata 条目缺失或重复。");
            var metadataBytes = await ReadEntryLimitedAsync(metadataEntry, MaxMetadataBytes, cancellationToken);
            ValidateMetadata(
                metadataBytes,
                item.Id,
                item.Name,
                item.Scope,
                item.EntryKind,
                item.Language);
            if (!string.Equals(ComputeSha256(metadataBytes), item.MetadataSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"脚本 {item.Id} 的 metadata 校验失败。");
            entryBytes[item.MetadataPath] = metadataBytes;
        }

        if (entries.Keys.Any(path => !referencedEntries.Contains(path)))
            throw new InvalidDataException("脚本共享包包含 manifest 未声明的文件。");
        return new ValidatedPackage(manifest, entryBytes);
    }

    private static async ValueTask<byte[]> ReadFileLimitedAsync(
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
            throw new InvalidDataException($"文件超过大小限制：{Path.GetFileName(path)}。");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.LongLength > maxBytes)
            throw new InvalidDataException($"文件超过大小限制：{Path.GetFileName(path)}。");
        return bytes;
    }

    private static async ValueTask<byte[]> ReadEntryLimitedAsync(
        ZipArchiveEntry entry,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (entry.Length > maxBytes)
            throw new InvalidDataException($"共享包条目超过大小限制：{entry.FullName}。");
        await using var source = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        await source.CopyToAsync(output, cancellationToken);
        if (output.Length > maxBytes)
            throw new InvalidDataException($"共享包条目超过大小限制：{entry.FullName}。");
        return output.ToArray();
    }

    private static async ValueTask WriteEntryAsync(
        ZipArchiveEntry entry,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async ValueTask WriteImportedFileAsync(
        string targetPath,
        byte[] bytes,
        ICollection<string> createdFiles,
        CancellationToken cancellationToken)
    {
        var tempPath = targetPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            File.Move(tempPath, targetPath, true);
            createdFiles.Add(targetPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static void ValidateMetadata(
        byte[] bytes,
        string scriptId,
        string scriptName,
        ScriptScope scope,
        ScriptEntryKind entryKind,
        string language)
    {
        ScriptFileMetadata metadata;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            metadata = JsonSerializer.Deserialize<ScriptFileMetadata>(bytes, MetadataJsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"脚本 {scriptId} 的 metadata 不是有效 JSON 对象。", exception);
        }

        var requiresIdentity = !string.Equals(language, "C#", StringComparison.OrdinalIgnoreCase);
        var effectiveEntryKind = metadata.EntryKind ?? ScriptEntryKind.Application;
        if ((metadata.Id is not null && !string.Equals(metadata.Id, scriptId, StringComparison.Ordinal))
            || (metadata.Name is not null && !string.Equals(metadata.Name, scriptName, StringComparison.Ordinal))
            || (metadata.Scope is not null && metadata.Scope != scope)
            || (metadata.EntryKind is not null && metadata.EntryKind != entryKind)
            || (requiresIdentity
                && (string.IsNullOrWhiteSpace(metadata.Id)
                    || string.IsNullOrWhiteSpace(metadata.Name)
                    || metadata.Scope is null
                    || effectiveEntryKind != entryKind)))
        {
            throw new InvalidDataException($"脚本 {scriptId} 的 metadata 与共享包描述不一致。");
        }
    }

    private static bool HasPortableMetadata(ScriptFileMetadata metadata) =>
        metadata.ApiVersion != ScriptApiVersion.V1
        || metadata.Id is not null
        || metadata.Name is not null
        || metadata.Description is not null
        || metadata.Engine is not null
        || metadata.Scope is not null
        || metadata.SupportedEditorTargets is { Count: > 0 }
        || metadata.EntryKind is not null
        || metadata.Schedule is not null
        || metadata.RunOnStartup
        || metadata.Triggers is { Count: > 0 }
        || metadata.DefaultArguments is { Count: > 0 }
        || metadata.TimeoutSeconds is not null;

    private static string GetArchiveDirectory(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string ComputeSha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSafeArchivePath(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.StartsWith('/')
        && !path.StartsWith('\\')
        && !path.Contains('\\')
        && path.Split('/').All(part => part.Length > 0 && part is not "." and not "..");

    private static bool IsSafeFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
        && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && fileName is not "." and not "..";

    private static string GetLanguage(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" => "C#",
        ".lua" => "Lua",
        ".py" => "Python",
        _ => throw new InvalidDataException($"不支持的脚本扩展名：{extension}。"),
    };

    private static void EnsureInsideRoot(string path, string root)
    {
        if (!ScriptCreationPolicy.IsInsideDirectory(Path.GetFullPath(path), root))
            throw new InvalidDataException("导入目标路径超出脚本目录。");
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record PackageManifest(
        string Format,
        int FormatVersion,
        DateTimeOffset CreatedAt,
        IReadOnlyList<PackageScriptItem> Scripts);

    private sealed record PackageScriptItem(
        string Id,
        string Name,
        ScriptScope Scope,
        ScriptEntryKind EntryKind,
        string Language,
        string OriginalFileName,
        string SourcePath,
        string? MetadataPath,
        string SourceSha256,
        string? MetadataSha256);

    private sealed record PreparedExportItem(
        ScriptShareExportItem Script,
        string FileName,
        string SourceEntryPath,
        string? MetadataEntryPath,
        byte[] SourceBytes,
        byte[]? MetadataBytes);

    private sealed record ValidatedPackage(
        PackageManifest Manifest,
        IReadOnlyDictionary<string, byte[]> EntryBytes);
}
