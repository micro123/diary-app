namespace Diary.Database;

/// <summary>
/// 插件数据库扩展初始化或迁移失败时抛出。区别于"无工厂支持"（返回 null），
/// 此异常表示扩展已选中但 <c>Initialize</c> 失败，宿主应将其映射为
/// <see cref="Diary.PluginBase.TrackerInstanceState.MigrationFailed"/>。
/// 不缓存，便于后续重试。
/// </summary>
public sealed class PluginExtensionInitException(string message, Exception? inner = null)
    : Exception(message, inner);
