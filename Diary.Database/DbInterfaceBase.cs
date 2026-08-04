using System.Data.Common;
using Diary.Core.Data.Base;
using Diary.Core.Data.Statistics;
using Diary.PluginBase;

namespace Diary.Database;

public abstract class DbInterfaceBase : IDisposable, IDbExtensionHost
{
    public string ProviderName => GetType().Name;
    protected readonly IDbFactory Factory;
    protected DbInterfaceBase(IDbFactory factory) => Factory = factory;

    // connect to db
    public abstract bool Connect();
    // check if initialized
    public abstract bool Initialized();
    // send keep alive heartbeat
    public abstract bool KeepAlive();
    // close connection
    public abstract void Close();

    // release all held resources (connection / data source / transaction)
    public abstract void Dispose();

    // data version
    public abstract uint GetDataVersion();
    // migrate tables
    public virtual bool UpdateTables(uint targetVersion)
    {
        var currentVersion = GetDataVersion();
        while (currentVersion != targetVersion)
        {
            var migration = Factory.GetMigration(currentVersion);
            if (migration == null)
                return false;
            migration.Up(this);
            currentVersion = GetDataVersion();
        }

        return true;
    }

    /// <summary>
    /// 执行多语句 DDL（版本迁移用）。命令由 <see cref="CreateCommand"/> 构建，
    /// 事务绑定等差异由 provider 在 <see cref="CreateCommand"/> 中处理。
    /// </summary>
    public bool ExecRaw(string sql)
    {
        using var cmd = CreateCommand(sql);
        cmd.ExecuteNonQuery();
        return true;
    }

    // work tag
    public abstract WorkTag CreateWorkTag(string name, bool primary, int color);
    public abstract bool UpdateWorkTag(WorkTag tag);
    public abstract bool DeleteWorkTag(WorkTag tag);
    public abstract ICollection<WorkTag> AllWorkTags();
    public abstract bool UpdateWorkTagId(int oldId, int newId);

    // work item
    public abstract WorkItem CreateWorkItem(string date, string comment);
    public abstract bool UpdateWorkItem(WorkItem item);
    public abstract bool DeleteWorkItem(WorkItem item);
    public abstract ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData);
    public abstract ICollection<WorkItem> GetWorkItemByDate(string data);
    public abstract bool UpdateWorkItemId(int oldId, int newId);

    // work note
    public abstract void WorkUpdateNote(WorkItem work, string content);
    public abstract void WorkDeleteNote(WorkItem work);
    public abstract string? WorkGetNote(WorkItem work);

    // batch queries for performance
    public virtual Dictionary<int, string> GetWorkNotesByDate(string date)
    {
        var result = new Dictionary<int, string>();
        foreach (var item in GetWorkItemByDate(date))
        {
            var note = WorkGetNote(item);
            if (note != null)
                result[item.Id] = note;
        }
        return result;
    }

    public virtual Dictionary<int, ICollection<WorkTag>> GetWorkTagsByDate(string date)
    {
        var result = new Dictionary<int, ICollection<WorkTag>>();
        foreach (var item in GetWorkItemByDate(date))
        {
            result[item.Id] = GetWorkItemTags(item);
        }
        return result;
    }

    // work item - work tag
    public abstract bool WorkItemAddTag(WorkItem item, WorkTag tag);
    public abstract bool WorkItemRemoveTag(WorkItem item, WorkTag tag);
    public abstract bool WorkItemCleanTags(WorkItem item);
    public abstract ICollection<WorkTag> GetWorkItemTags(WorkItem item);

    private readonly Dictionary<(Type Type, string InstanceId), object?> _extensions = new();

    /// <summary>获取 provider 提供的可选数据库扩展；核心库不引用具体 tracker 类型。</summary>
    public T? GetExtension<T>(
        string instanceId = "redmine.default",
        IEnumerable<IPluginMigration>? migrations = null) where T : class
    {
        var type = typeof(T);
        var key = (type, instanceId);
        if (!_extensions.TryGetValue(key, out var extension))
        {
            extension = CreateExtension(type, instanceId, migrations?.ToArray() ?? Array.Empty<IPluginMigration>());
            _extensions[key] = extension;
        }

        return extension as T;
    }

    protected virtual object? CreateExtension(
        Type extensionType,
        string instanceId,
        IReadOnlyList<IPluginMigration> migrations)
    {
        foreach (var factory in DbExtensionFactoryLoader.Factories)
        {
            if (factory.Supports(extensionType, ProviderName))
                return factory.Create(this, instanceId, migrations);
        }

        return null;
    }

    // statistics
    public abstract StatisticsResult GetStatistics(string beginDate, string endDate);
    public virtual StatisticsResult GetStatistics()
    {
        // get date range
        const string sql = "SELECT min(create_date), max(create_date) FROM work_items;";
        using var cmd = CreateCommand(sql);
        using var reader = cmd.ExecuteReader();
        if (reader.Read() && !reader.IsDBNull(0))
        {
            var beginDate = ReadString(reader, 0);
            var endDate = ReadString(reader, 1);
            return GetStatistics(beginDate, endDate);
        }

        // empty result
        return new StatisticsResult()
        {
            DateBegin = string.Empty,
            DateEnd = string.Empty,
            Total = 0,
            PrimaryTags = Array.Empty<TagTime>(),
        };
    }
    public abstract ICollection<WorkItem> GetWorkItemsByTagAndDate(string dateBegin, string dateEnd, int l1, int l2 = 0);

    // migrate use
    public abstract bool DropData();
    public abstract bool BeginTransaction();
    public abstract bool CommitTransaction();
    public abstract bool RollbackTransaction();

    #region provider primitives

    /// <summary>
    /// 构造一个已设好 CommandText、按需绑好当前事务的命令，调用方可直接加参数。
    /// </summary>
    protected abstract DbCommand CreateCommand(string sql);

    /// <summary>
    /// 读取字符串列。provider 封装 CHAR padding 处理差异
    /// （SQLite 不 trim；PostgreSQL 的 CHAR 列返回值带尾随空格需 TrimEnd）。
    /// </summary>
    protected abstract string ReadString(DbDataReader reader, int ordinal);

    /// <summary>
    /// 绑定一个参数。SQLite 按 <paramref name="name"/> 命名绑定（须匹配 SQL 的 $name 占位符）；
    /// PostgreSQL 按位置绑定（忽略 <paramref name="name"/>，args 顺序须匹配 SQL 的 $1..$n）。
    /// </summary>
    protected abstract void BindParameter(DbCommand command, string name, object? value);

    #endregion

    #region orchestration helpers

    /// <summary>执行查询，对每行调用 <paramref name="map"/> 收集为列表。</summary>
    protected List<T> Query<T>(string sql, Func<DbDataReader, T> map, params (string Name, object? Value)[] args)
    {
        var result = new List<T>();
        using var cmd = CreateCommand(sql);
        foreach (var (name, value) in args)
            BindParameter(cmd, name, value);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(map(reader));
        return result;
    }

    /// <summary>读取首行并映射；无行返回 null。</summary>
    protected T? QueryFirst<T>(string sql, Func<DbDataReader, T> map, params (string Name, object? Value)[] args)
        where T : class
    {
        using var cmd = CreateCommand(sql);
        foreach (var (name, value) in args)
            BindParameter(cmd, name, value);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? map(reader) : null;
    }

    /// <summary>执行非查询，返回受影响行数。</summary>
    protected int Execute(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = CreateCommand(sql);
        foreach (var (name, value) in args)
            BindParameter(cmd, name, value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>执行标量查询，返回首行首列。</summary>
    protected object? ExecuteScalar(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = CreateCommand(sql);
        foreach (var (name, value) in args)
            BindParameter(cmd, name, value);
        return cmd.ExecuteScalar();
    }

    /// <summary>是否存在匹配行。</summary>
    protected bool Exists(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = CreateCommand(sql);
        foreach (var (name, value) in args)
            BindParameter(cmd, name, value);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    #endregion

    #region IDbExtensionHost — 把 protected helpers 提升为公开接口，供独立扩展类使用

    List<T> IDbExtensionHost.Query<T>(string sql, Func<DbDataReader, T> map, params (string Name, object? Value)[] args)
        => Query(sql, map, args);

    T? IDbExtensionHost.QueryFirst<T>(string sql, Func<DbDataReader, T> map, params (string Name, object? Value)[] args)
        where T : class
        => QueryFirst(sql, map, args);

    int IDbExtensionHost.Execute(string sql, params (string Name, object? Value)[] args)
        => Execute(sql, args);

    object? IDbExtensionHost.ExecuteScalar(string sql, params (string Name, object? Value)[] args)
        => ExecuteScalar(sql, args);

    bool IDbExtensionHost.Exists(string sql, params (string Name, object? Value)[] args)
        => Exists(sql, args);

    bool IDbExtensionHost.ExecRaw(string sql) => ExecRaw(sql);

    string IDbExtensionHost.ReadString(DbDataReader reader, int ordinal) => ReadString(reader, ordinal);

    #endregion

    #region mappers

    protected WorkTag MapWorkTag(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Name = ReadString(r, 1),
        Color = r.GetInt32(2),
        Level = (TagLevels)r.GetInt32(3),
        Disabled = r.GetInt32(4) != 0,
    };

    protected WorkItem MapWorkItem(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        CreateDate = ReadString(r, 1),
        Comment = ReadString(r, 2),
        Time = r.GetFloat(3),
        Priority = (WorkPriorities)r.GetInt32(4),
    };

    #endregion
}
