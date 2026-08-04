namespace Diary.PluginBase;

/// <summary>
/// 插件 manifest。主程序在注册服务和加载 UI 前据此做兼容性检查（文档 §5）。
/// </summary>
public sealed record PluginManifest
{
    /// <summary>稳定插件标识，如 <c>tracker.redmine</c>。</summary>
    public required string Id { get; init; }
    /// <summary>插件版本（语义化），仅用于显示与升级判断。</summary>
    public required string Version { get; init; }

    /// <summary>插件 API 契约版本；破坏性变化递增。</summary>
    public int ApiVersion { get; init; }
    /// <summary>是否支持同一插件类型创建多个实例。</summary>
    public bool SupportsMultipleInstances { get; init; }
    /// <summary>所需核心数据库最低版本。</summary>
    public uint MinCoreDataVersion { get; init; }
    /// <summary>明确不兼容时的核心数据库最高版本；null 表示无上限。</summary>
    public uint? MaxCoreDataVersion { get; init; }

    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = Array.Empty<PluginDependency>();
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();
}
