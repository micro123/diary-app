using Newtonsoft.Json;

namespace Diary.Core.ConfigurationTools;

public sealed class ConfigurationTransferService
{
    private const int ExportFormatVersion = 1;

    public ConfigurationExportResult Export(
        ConfigurationDocumentStore store,
        IEnumerable<ConfigurationFileDescriptor> descriptors,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var exported = new List<ConfigurationExportEntry>();
        var failures = new List<ConfigurationTransferFailure>();
        foreach (var descriptor in descriptors)
        {
            var document = store.Load(descriptor);
            if (document.Status == ConfigurationDocumentStatus.Missing)
                continue;
            if (document.Status != ConfigurationDocumentStatus.Loaded || document.Json is null)
            {
                failures.Add(new ConfigurationTransferFailure(descriptor.Id, document.Error ?? "配置无法读取。"));
                continue;
            }

            var exportPath = GetExportRelativePath(descriptor);
            var target = ResolveContainedPath(root, exportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, document.Json + Environment.NewLine, new System.Text.UTF8Encoding(false));
            exported.Add(new ConfigurationExportEntry(
                descriptor.Id,
                descriptor.DisplayName,
                exportPath.Replace(Path.DirectorySeparatorChar, '/'),
                descriptor.RelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                descriptor.IsEncrypted));
        }

        var manifest = new ConfigurationExportManifest(
            ExportFormatVersion,
            DateTimeOffset.Now,
            Environment.OSVersion.Platform.ToString(),
            exported);
        File.WriteAllText(
            Path.Combine(root, "manifest.json"),
            JsonConvert.SerializeObject(manifest, Formatting.Indented) + Environment.NewLine,
            new System.Text.UTF8Encoding(false));
        return new ConfigurationExportResult(root, exported.Count, failures);
    }

    public ConfigurationImportResult Import(
        ConfigurationDocumentStore store,
        IReadOnlyDictionary<string, ConfigurationFileDescriptor> descriptors,
        string inputDirectory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);

        var root = Path.GetFullPath(inputDirectory);
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("导入目录缺少 manifest.json。", manifestPath);
        var manifest = JsonConvert.DeserializeObject<ConfigurationExportManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("导入清单为空。");
        if (manifest.FormatVersion != ExportFormatVersion)
            throw new InvalidDataException($"不支持的导出格式版本：{manifest.FormatVersion}。");

        var snapshot = store.CreateSnapshot(descriptors.Values);
        var imported = 0;
        var failures = new List<ConfigurationTransferFailure>();
        foreach (var entry in manifest.Configurations)
        {
            if (!descriptors.TryGetValue(entry.Id, out var descriptor))
            {
                failures.Add(new ConfigurationTransferFailure(entry.Id, "当前版本不识别此配置。"));
                continue;
            }
            try
            {
                var source = ResolveContainedPath(root, entry.ExportPath);
                var json = File.ReadAllText(source);
                var current = store.Load(descriptor);
                if (current.Status == ConfigurationDocumentStatus.Unreadable
                    && (!descriptor.IsEncrypted || store.MasterKeyExists))
                {
                    throw new InvalidOperationException("目标配置不可读取，已阻止覆盖。请先处理原配置或将其移出目录。");
                }
                store.Save(descriptor, json, current.Fingerprint);
                imported++;
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException
                                               or JsonException
                                               or InvalidOperationException)
            {
                failures.Add(new ConfigurationTransferFailure(entry.Id, exception.Message));
            }
        }
        return new ConfigurationImportResult(snapshot, imported, failures);
    }

    private static string GetExportRelativePath(ConfigurationFileDescriptor descriptor)
    {
        var safeId = string.Concat(descriptor.Id.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
        return Path.Combine(descriptor.Category, safeId + ".json");
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
            throw new InvalidDataException("导入清单包含绝对路径。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!path.StartsWith(fullRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new InvalidDataException("导入清单路径超出导入目录。");
        }
        return path;
    }
}

public sealed record ConfigurationExportManifest(
    int FormatVersion,
    DateTimeOffset ExportedAt,
    string SourcePlatform,
    IReadOnlyList<ConfigurationExportEntry> Configurations);

public sealed record ConfigurationExportEntry(
    string Id,
    string DisplayName,
    string ExportPath,
    string SourcePath,
    bool ContainsSecrets);

public sealed record ConfigurationTransferFailure(string Id, string Error);

public sealed record ConfigurationExportResult(
    string OutputDirectory,
    int ExportedCount,
    IReadOnlyList<ConfigurationTransferFailure> Failures);

public sealed record ConfigurationImportResult(
    string SnapshotDirectory,
    int ImportedCount,
    IReadOnlyList<ConfigurationTransferFailure> Failures);
