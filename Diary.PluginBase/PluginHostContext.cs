namespace Diary.PluginBase;

/// <summary>
/// 主程序传给插件的运行时上下文。插件只依赖抽象对象，不依赖 Diary.App 的具体实现。
/// </summary>
public sealed record PluginHostContext(
    object Database,
    object Configuration)
{
    /// <summary>宿主从通用配置存储枚举出的实例项；旧调用方默认为空。</summary>
    public IReadOnlyList<PluginInstanceConfiguration> InstanceConfigurations { get; init; }
        = Array.Empty<PluginInstanceConfiguration>();
}
