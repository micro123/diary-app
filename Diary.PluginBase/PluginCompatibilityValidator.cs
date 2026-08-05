namespace Diary.PluginBase;

public sealed record PluginCompatibilityContext(
    int MinApiVersion,
    int MaxApiVersion,
    uint CoreDataVersion,
    IReadOnlySet<string> Capabilities)
{
    /// <summary>本次启动已发现的插件 ID 集合，用于必选依赖存在性检查（文档 §5.2）。</summary>
    public IReadOnlySet<string> AvailablePluginIds { get; init; } = new HashSet<string>();

    /// <summary>
    /// 本次启动已发现的插件 manifest，用于依赖版本范围检查。
    /// 为空时仍兼容只提供 <see cref="AvailablePluginIds"/> 的旧调用方。
    /// </summary>
    public IReadOnlyDictionary<string, PluginManifest> AvailablePlugins { get; init; }
        = new Dictionary<string, PluginManifest>();
}

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

        // 必选依赖必须已发现；可选依赖缺失时降级，不阻断（§5.2）
        foreach (var dep in manifest.Dependencies)
        {
            if (!context.AvailablePluginIds.Contains(dep.PluginId))
            {
                if (dep.Optional)
                    continue;
                error = $"缺少必选依赖：{dep.PluginId}";
                return false;
            }

            // 旧宿主可能只提供 ID 集合，无法进行版本检查；真实 App 会同时提供 manifest。
            if (context.AvailablePlugins.TryGetValue(dep.PluginId, out var dependency)
                && !PluginVersionRange.IsSatisfied(
                    dependency.Version, dep.VersionRange, out var rangeError))
            {
                error = $"依赖 {dep.PluginId} 版本不满足范围 {dep.VersionRange}：{rangeError}";
                return false;
            }
        }

        error = null;
        return true;
    }
}
