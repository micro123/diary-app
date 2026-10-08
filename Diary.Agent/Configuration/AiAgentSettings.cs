using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.Agent.Web;

namespace Diary.Agent.Configuration;

public sealed record AiAgentToolSettings
{
    public bool Diary { get; init; } = true;

    public bool WebSearch { get; init; }

    public bool WebFetch { get; init; }

    public bool Mcp { get; init; }

    public bool WorkItemWrite { get; init; }
}

public sealed record AiAgentSettings
{
    public AiAgentToolSettings EnabledTools { get; init; } = new();

    public string? DefaultProfileId { get; init; }

    public IReadOnlyList<AiConnectionProfile> Profiles { get; init; } = [];

    public IReadOnlyDictionary<string, AiConnectionCapabilities> Capabilities { get; init; } =
        new Dictionary<string, AiConnectionCapabilities>(StringComparer.Ordinal);

    public IReadOnlyList<McpServerProfile> McpServers { get; init; } = [];

    public WebAccessPolicy WebAccess { get; init; } = new();

    public AiProxyConfiguration DefaultProxy { get; init; } = new() { Mode = AiProxyMode.System };
}

public enum AiSettingsLoadStatus
{
    Missing,
    Loaded,
    Unreadable,
}

public sealed record AiSettingsLoadResult(
    AiSettingsLoadStatus Status,
    AiAgentSettings? Settings,
    string? Error = null);

public sealed class AiConnectionStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public AiSettingsLoadResult Load()
    {
        if (!File.Exists(path))
            return new AiSettingsLoadResult(AiSettingsLoadStatus.Missing, null);
        try
        {
            using var stream = File.OpenRead(path);
            var envelope = JsonSerializer.Deserialize<SettingsEnvelope>(stream, JsonOptions)
                ?? throw new JsonException("AI 设置文件为空。");
            if (envelope.SchemaVersion != 1)
                throw new JsonException($"不支持的 AI 设置 schemaVersion：{envelope.SchemaVersion}。");
            ValidateSettings(envelope.Settings);
            return new AiSettingsLoadResult(AiSettingsLoadStatus.Loaded, envelope.Settings);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AiSettingsLoadResult(AiSettingsLoadStatus.Unreadable, null, exception.Message);
        }
    }

    public void Save(AiAgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Load().Status == AiSettingsLoadStatus.Unreadable)
            throw new InvalidOperationException("AI 设置文件不可读取，已阻止覆盖。");
        ValidateSettings(settings);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("AI 设置路径缺少父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new SettingsEnvelope(1, settings), JsonOptions);
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

    private static void ValidateSettings(AiAgentSettings settings)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in settings.Profiles)
        {
            var errors = AiConnectionProfileValidator.Validate(profile);
            if (errors.Count > 0)
                throw new JsonException($"连接 {profile.Id} 无效：{string.Join(" ", errors)}");
            if (!ids.Add(profile.Id))
                throw new JsonException($"连接 ID 重复：{profile.Id}。");
        }
        if (settings.DefaultProfileId is not null && !ids.Contains(settings.DefaultProfileId))
            throw new JsonException("默认连接 ID 不存在于 profiles 中。");
        if (settings.Capabilities.Keys.Any(id => !ids.Contains(id)))
            throw new JsonException("能力缓存包含不存在的连接 ID。");
        var mcpIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in settings.McpServers)
        {
            var errors = McpServerProfileValidator.Validate(server);
            if (errors.Count > 0)
                throw new JsonException($"MCP Server {server.Id} 无效：{string.Join(" ", errors)}");
            if (!mcpIds.Add(server.Id))
                throw new JsonException($"MCP Server ID 重复：{server.Id}。");
        }
        var webErrors = WebAccessPolicyValidator.Validate(settings.WebAccess);
        if (webErrors.Count > 0)
            throw new JsonException($"网页访问策略无效：{string.Join(" ", webErrors)}");
        if (settings.DefaultProxy.Mode == AiProxyMode.Inherit)
            throw new JsonException("AI 默认代理不能继续设置为继承。");
        var proxyErrors = AiConnectionProfileValidator.ValidateProxyConfiguration(settings.DefaultProxy);
        if (proxyErrors.Count > 0)
            throw new JsonException($"AI 默认代理无效：{string.Join(" ", proxyErrors)}");
    }

    private sealed record SettingsEnvelope(int SchemaVersion, AiAgentSettings Settings);
}
