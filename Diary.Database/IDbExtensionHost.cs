using System.Data.Common;

namespace Diary.Database;

/// <summary>
/// 中性数据访问宿主：把 <see cref="DbInterfaceBase"/> 的 orchestration helpers
/// （<see cref="Query{T}"/>/<see cref="QueryFirst{T}"/>/<see cref="Execute"/> 等）
/// 与 <see cref="ReadString"/> 提升为公开接口，让独立扩展类（如 RedMineDb）
/// 能跨 provider 用同一套 high-level 原语跑 SQL，而不必依赖具体的
/// <see cref="DbInterfaceBase"/> 子类或其 protected 成员。
///
/// provider 差异（命名 vs 位置参数、CHAR padding）由 <see cref="BindParameter"/>
/// 与 <see cref="ReadString"/> 在 provider 内封装，扩展类只写 SQL 占位符即可。
/// </summary>
public interface IDbExtensionHost
{
    /// <summary>执行查询，对每行调用 <paramref name="map"/> 收集为列表。</summary>
    List<T> Query<T>(string sql, Func<DbDataReader, T> map, params (string Name, object? Value)[] args);

    /// <summary>读取首行并映射；无行返回 null。</summary>
    T? QueryFirst<T>(string sql, Func<DbDataReader, T> map, params (string Name, object? Value)[] args)
        where T : class;

    /// <summary>执行非查询，返回受影响行数。</summary>
    int Execute(string sql, params (string Name, object? Value)[] args);

    /// <summary>执行标量查询，返回首行首列。</summary>
    object? ExecuteScalar(string sql, params (string Name, object? Value)[] args);

    /// <summary>是否存在匹配行。</summary>
    bool Exists(string sql, params (string Name, object? Value)[] args);

    /// <summary>执行多语句 DDL。</summary>
    bool ExecRaw(string sql);

    /// <summary>读取字符串列。provider 封装 CHAR padding 处理差异。</summary>
    string ReadString(DbDataReader reader, int ordinal);
}
