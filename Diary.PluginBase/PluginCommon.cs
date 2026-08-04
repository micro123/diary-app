namespace Diary.PluginBase;

/// <summary>插件对其他插件的依赖（文档 §5.2）。</summary>
public sealed record PluginDependency(
    string PluginId,
    string VersionRange,
    bool Optional = false);

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
