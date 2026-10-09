using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diary.ModuleBase;

public sealed class ModuleStateStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public ModuleStateLoadResult Load()
    {
        if (!File.Exists(path))
            return new ModuleStateLoadResult(false, false, new Dictionary<string, bool>(StringComparer.Ordinal));

        try
        {
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<ModuleStateDocument>(stream, JsonOptions)
                ?? throw new JsonException("模块状态文件为空。");
            if (document.SchemaVersion != 1)
                throw new JsonException($"不支持的模块状态 schemaVersion：{document.SchemaVersion}。");

            var states = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var pair in document.Modules)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                    throw new JsonException("模块状态包含无效模块 ID 或空状态。");
                states.Add(pair.Key, pair.Value.Enabled);
            }
            return new ModuleStateLoadResult(true, false, states);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ModuleStateLoadResult(true, true, new Dictionary<string, bool>(StringComparer.Ordinal), exception.Message);
        }
    }

    public void Save(IReadOnlyDictionary<string, bool> moduleStates)
    {
        ArgumentNullException.ThrowIfNull(moduleStates);
        var current = Load();
        if (current.IsCorrupt)
            throw new InvalidOperationException("模块状态文件不可读取，已阻止覆盖。");

        var modules = new Dictionary<string, ModuleStateEntry>(StringComparer.Ordinal);
        foreach (var pair in moduleStates)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                throw new ArgumentException("模块状态包含无效模块 ID。", nameof(moduleStates));
            modules.Add(pair.Key, new ModuleStateEntry { Enabled = pair.Value });
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("模块状态路径缺少父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new ModuleStateDocument
                {
                    SchemaVersion = 1,
                    Modules = modules,
                }, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record ModuleStateDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("modules")]
        public Dictionary<string, ModuleStateEntry> Modules { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed record ModuleStateEntry
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }
    }
}

public sealed record ModuleStateLoadResult(
    bool Exists,
    bool IsCorrupt,
    IReadOnlyDictionary<string, bool> ModuleStates,
    string? Error = null)
{
    public bool IsEnabled(AppModuleManifest manifest)
    {
        if (IsCorrupt)
            return false;
        return ModuleStates.TryGetValue(manifest.Id, out var enabled)
            ? enabled
            : manifest.EnabledByDefault;
    }
}
