namespace Diary.PluginBase;

/// <summary>插件配置 schema 迁移失败，原始配置文件未被覆盖。</summary>
public sealed class PluginConfigurationMigrationException(
    string pluginId,
    int fromVersion,
    int targetVersion,
    string message,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string PluginId { get; } = pluginId;
    public int FromVersion { get; } = fromVersion;
    public int TargetVersion { get; } = targetVersion;
}
