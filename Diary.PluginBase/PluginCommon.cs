namespace Diary.PluginBase;

/// <summary>插件对其他插件的依赖（文档 §5.2）。</summary>
public sealed record PluginDependency(
    string PluginId,
    string VersionRange,
    bool Optional = false);

/// <summary>插件配置中的一个实例项，由宿主统一枚举和传递（文档阶段 1）。</summary>
public sealed record PluginInstanceConfiguration(
    string InstanceId,
    object Configuration,
    bool Enabled = true,
    string? DisplayName = null);

/// <summary>
/// 插件实例配置存储契约。插件可以返回全部配置项，宿主只创建 Enabled 项。
/// 默认实现保持旧插件兼容；旧插件仍可直接实现 GetInstanceRegistrations。
/// </summary>
public interface IPluginInstanceConfigurationStore
{
    IEnumerable<PluginInstanceConfiguration> GetInstanceConfigurations(object configuration)
        => Array.Empty<PluginInstanceConfiguration>();
}

/// <summary>插件生命周期状态（文档 §6）。</summary>
public enum PluginState
{
    Discovered,
    Installed,
    Compatible,
    MigrationRequired,
    Enabled,
    Disabled,
    Blocked,
    MigrationFailed,
    ConfigurationMigrationFailed,
}

/// <summary>
/// 单个 tracker 实例的生命周期状态（文档 §3.3、§6）。与 <see cref="PluginState"/> 不同：
/// 后者描述插件整体加载，本状态描述具体实例在宿主中是否可用。
/// </summary>
public enum TrackerInstanceState
{
    /// <summary>已启用：实例已创建，参与编辑器、模板和 UI。</summary>
    Enabled,

    /// <summary>用户主动禁用（本期不产生，预留）。</summary>
    Disabled,

    /// <summary>插件未提供对应 provider 的数据库扩展。</summary>
    NotConfigured,

    /// <summary>数据库初始化或迁移失败，只禁用当前实例。</summary>
    MigrationFailed,

    /// <summary>远程连接失败（本期不产生，预留）。</summary>
    ConnectionFailed,

    /// <summary>实例创建抛异常或身份不匹配。</summary>
    Blocked,
}

/// <summary>远程上传统一结果（文档 §10.2）。</summary>
public sealed record TrackerOperationResult(
    bool Success,
    string? Error = null,
    string? RemoteId = null);

/// <summary>数据库能力常量（文档 §15），插件 manifest 在 RequiredCapabilities 中声明。</summary>
public static class PluginCapabilities
{
    public const string SqlTransactions = nameof(SqlTransactions);
    public const string ForeignKeys = nameof(ForeignKeys);
    public const string ReturningClause = nameof(ReturningClause);
    public const string MultipleStatementExecution = nameof(MultipleStatementExecution);
}
