namespace Diary.PluginBase;

/// <summary>
/// 主程序传给插件的运行时上下文。插件只依赖抽象对象，不依赖 Diary.App 的具体实现。
/// </summary>
public sealed record PluginHostContext(
    object Database,
    object Configuration);
