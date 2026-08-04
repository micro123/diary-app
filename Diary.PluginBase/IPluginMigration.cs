using System.Data.Common;

namespace Diary.PluginBase;

/// <summary>插件数据库迁移步骤（文档 §8.2）。</summary>
public interface IPluginMigration
{
    string PluginId { get; }
    uint FromVersion { get; init; }
    uint ToVersion { get; init; }

    bool Up(IPluginMigrationContext context);
}

/// <summary>
/// 迁移上下文（文档 §8.2）。provider 无关能力，不暴露具体 Connection 类型。
/// </summary>
public interface IPluginMigrationContext
{
    string ProviderName { get; }
    uint CoreDataVersion { get; }

    bool ExecRaw(string sql);
    List<T> Query<T>(string sql, Func<DbDataReader, T> map, params object[] args);
}
