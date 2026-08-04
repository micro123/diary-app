namespace Diary.PluginBase;

public sealed record PluginCompatibilityContext(
    int MinApiVersion,
    int MaxApiVersion,
    uint CoreDataVersion,
    IReadOnlySet<string> Capabilities);

public static class PluginCompatibilityValidator
{
    public static bool Validate(
        PluginManifest manifest,
        PluginCompatibilityContext context,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(context);

        if (manifest.ApiVersion < context.MinApiVersion
            || manifest.ApiVersion > context.MaxApiVersion)
        {
            error = $"插件 API 版本 {manifest.ApiVersion} 不受支持";
            return false;
        }

        if (context.CoreDataVersion < manifest.MinCoreDataVersion
            || manifest.MaxCoreDataVersion is not null
                && context.CoreDataVersion > manifest.MaxCoreDataVersion)
        {
            error = "核心数据库版本不兼容";
            return false;
        }

        var missing = manifest.RequiredCapabilities
            .Where(capability => !context.Capabilities.Contains(capability))
            .ToArray();
        if (missing.Length > 0)
        {
            error = $"缺少数据库能力：{string.Join(", ", missing)}";
            return false;
        }

        error = null;
        return true;
    }
}
