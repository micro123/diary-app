using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diary.Mcp.Remote;

public sealed record RemoteMcpModuleSettings
{
    public int RequestsPerMinute { get; init; } = 60;
}

internal static class RemoteMcpModuleSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static RemoteMcpModuleSettings LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new RemoteMcpModuleSettings();
            Save(path, defaults);
            return defaults;
        }

        using var stream = File.OpenRead(path);
        var settings = JsonSerializer.Deserialize<RemoteMcpModuleSettings>(stream, JsonOptions)
            ?? throw new JsonException("远程 MCP 模块设置为空。");
        Validate(settings);
        return settings;
    }

    private static void Save(string path, RemoteMcpModuleSettings settings)
    {
        Validate(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("远程 MCP 模块设置路径缺少父目录。"));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, settings, JsonOptions);
        stream.Flush(flushToDisk: true);
    }

    private static void Validate(RemoteMcpModuleSettings settings)
    {
        if (settings.RequestsPerMinute is < 1 or > 6000)
            throw new JsonException("requestsPerMinute 必须在 1 到 6000 之间。");
    }
}
