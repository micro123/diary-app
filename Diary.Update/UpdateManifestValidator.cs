using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Diary.Update;

public static class UpdateManifestValidator
{
    private static readonly HashSet<string> SupportedComponents = new(StringComparer.Ordinal)
    {
        "app",
        "worker",
        "updater",
    };

    public static void Validate(UpdateManifestEnvelope envelope, string channel, string rid, string flavor)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var manifest = envelope.Manifest ?? throw new InvalidDataException("更新响应缺少 manifest。");
        var package = envelope.FullPackage ?? throw new InvalidDataException("更新响应缺少 fullPackage。");
        if (manifest.ManifestFormatVersion != 1)
            throw new InvalidDataException($"不支持的更新清单版本：{manifest.ManifestFormatVersion}");
        if (manifest.Sequence < 0 || manifest.MinIncrementalSequence < 0)
            throw new InvalidDataException("更新清单序号非法。");
        if (string.IsNullOrWhiteSpace(manifest.VersionId)
            || string.IsNullOrWhiteSpace(manifest.DataVersion)
            || string.IsNullOrWhiteSpace(manifest.ManifestContentId))
            throw new InvalidDataException("更新清单版本信息为空。");
        if (!string.Equals(manifest.Channel, channel, StringComparison.Ordinal)
            || !string.Equals(manifest.Rid, rid, StringComparison.Ordinal)
            || !string.Equals(manifest.Flavor, flavor, StringComparison.Ordinal))
        {
            throw new InvalidDataException("更新清单维度与请求不匹配。");
        }
        if (!manifest.ManifestContentId.StartsWith("sha256:", StringComparison.Ordinal)
            || !UpdateHash.IsSha256(manifest.ManifestContentId["sha256:".Length..]))
        {
            throw new InvalidDataException("manifestContentId 非法。");
        }
        if (package.Size <= 0 || !UpdateHash.IsSha256(package.Sha256))
            throw new InvalidDataException("完整包描述非法。");
        if (manifest.Files is null || manifest.Files.Count == 0)
            throw new InvalidDataException("更新清单不能是空文件集合。");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        var windowsPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        foreach (var file in manifest.Files)
        {
            if (file is null)
                throw new InvalidDataException("更新清单包含空文件项。");
            var path = UpdatePathPolicy.NormalizeRelative(file.Path, nameof(file.Path));
            if (!paths.Add(path) || rid == "win-x64" && !windowsPaths.Add(path))
                throw new InvalidDataException($"更新清单包含重复路径：{path}");
            if (previousPath is not null && string.CompareOrdinal(previousPath, path) >= 0)
                throw new InvalidDataException("更新清单文件必须按 path 严格升序排列。");
            previousPath = path;
            if (file.Size < 0 || !UpdateHash.IsSha256(file.Sha256))
                throw new InvalidDataException($"更新文件大小或 SHA-256 非法：{path}");
            if (!SupportedComponents.Contains(file.Component))
                throw new InvalidDataException($"更新文件 component 非法：{path}");
            if (rid == "win-x64" && file.Executable)
                throw new InvalidDataException($"Windows 更新文件不能声明 Unix executable：{path}");
        }
        if (!string.Equals(manifest.ManifestContentId, ComputeContentId(manifest), StringComparison.Ordinal))
            throw new InvalidDataException("manifestContentId 与文件集合不匹配。");
    }

    public static string ComputeContentId(UpdateManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("rid", manifest.Rid);
            writer.WriteString("flavor", manifest.Flavor);
            writer.WriteStartArray("files");
            foreach (var file in manifest.Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteNumber("size", file.Size);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteString("component", file.Component);
                writer.WriteBoolean("executable", file.Executable);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return $"sha256:{Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))}";
    }
}
