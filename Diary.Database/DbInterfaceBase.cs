using System.Data.Common;
using System.Text.Json;
using Diary.Core.Data.Base;
using Diary.Core.Data.Statistics;
using Diary.PluginBase;

namespace Diary.Database;

public abstract partial class DbInterfaceBase : IDisposable, IDbExtensionHost
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

    /// <summary>
    /// 在核心数据迁移前创建 provider 可提供的备份。
    /// 不支持本地备份的 provider 返回成功且 <paramref name="backupPath"/> 为 <see langword="null"/>。
    /// </summary>
    public virtual bool TryCreateMigrationBackup(
        uint targetVersion,
        out string? backupPath,
        out string? error)
    {
        backupPath = null;
        error = null;
        return true;
    }

    // migrate tables
    public virtual bool UpdateTables(uint targetVersion) => MigrateTo(targetVersion).Success;

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
    public abstract WorkTag CreateWorkTag(
        string name,
        bool primary,
        int color,
        IReadOnlyDictionary<string, string>? metadata = null);
    public abstract bool UpdateWorkTag(WorkTag tag);
    public abstract bool DeleteWorkTag(WorkTag tag);
    public abstract ICollection<WorkTag> AllWorkTags();
    public abstract bool UpdateWorkTagId(int oldId, int newId);

    // tag extra fields
    public virtual ICollection<TagExtraFieldDefinition> GetTagExtraFieldDefinitions(
        int tagId, bool includeDisabled = false) => Array.Empty<TagExtraFieldDefinition>();
    public virtual ICollection<TagExtraFieldDefinition> GetAllTagExtraFieldDefinitions(
        bool includeDisabled = false) => Array.Empty<TagExtraFieldDefinition>();
    public virtual bool CreateTagExtraFieldDefinition(TagExtraFieldDefinition definition) => false;
    public virtual bool UpdateTagExtraFieldDefinition(TagExtraFieldDefinition definition) => false;
    public virtual bool IsTagExtraFieldKeyAvailable(string fieldKey, string? excludingFieldId = null) => true;

    // work item
    public abstract WorkItem CreateWorkItem(string date, string comment);
    public abstract bool UpdateWorkItem(WorkItem item);
    public abstract bool DeleteWorkItem(WorkItem item);
    public abstract ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData);
    public abstract ICollection<WorkItem> GetWorkItemByDate(string data);
    public abstract ICollection<WorkItem> QueryWorkItems(WorkItemQuery query);
    public abstract bool UpdateWorkItemId(int oldId, int newId);
    public abstract bool MarkWorkItemReadOnly(WorkItem item);
    public virtual ICollection<WorkItemExtraField> GetWorkItemExtraFields(WorkItem item) =>
        Array.Empty<WorkItemExtraField>();
    public virtual Dictionary<int, ICollection<WorkItemExtraField>> GetWorkItemExtraFieldsByWorkItemIds(
        IReadOnlyCollection<int> workItemIds)
    {
        ArgumentNullException.ThrowIfNull(workItemIds);
        var result = new Dictionary<int, ICollection<WorkItemExtraField>>();
        foreach (var id in workItemIds.Where(id => id > 0).Distinct())
        {
            var fields = GetWorkItemExtraFields(new WorkItem { Id = id });
            if (fields.Count > 0)
                result[id] = fields;
        }
        return result;
    }
    public virtual bool SaveWorkItemExtraFieldValues(
        int workItemId, IReadOnlyCollection<WorkItemExtraFieldValue> values) => true;

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

    public virtual Dictionary<int, string> GetWorkNotesByWorkItemIds(
        IReadOnlyCollection<int> workItemIds)
    {
        ArgumentNullException.ThrowIfNull(workItemIds);
        var result = new Dictionary<int, string>();
        foreach (var id in workItemIds.Where(id => id != 0).Distinct())
        {
            var note = WorkGetNote(new WorkItem { Id = id });
            if (note is not null)
                result[id] = note;
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

    public virtual Dictionary<int, ICollection<WorkTag>> GetWorkTagsByWorkItemIds(
        IReadOnlyCollection<int> workItemIds)
    {
        ArgumentNullException.ThrowIfNull(workItemIds);
        var result = new Dictionary<int, ICollection<WorkTag>>();
        foreach (var id in workItemIds.Where(id => id != 0).Distinct())
        {
            var tags = GetWorkItemTags(new WorkItem { Id = id });
            if (tags.Count > 0)
                result[id] = tags;
        }
        return result;
    }

    // work item - work tag
    public abstract bool WorkItemAddTag(WorkItem item, WorkTag tag);
    public abstract bool WorkItemRemoveTag(WorkItem item, WorkTag tag);
    public abstract bool WorkItemCleanTags(WorkItem item);
    public abstract ICollection<WorkTag> GetWorkItemTags(WorkItem item);

    private readonly Dictionary<(Type Type, string InstanceId), object?> _extensions = new();

    /// <summary>
    /// 获取 provider 提供的可选数据库扩展；核心库不引用具体 tracker 类型。
    /// 无工厂支持时返回并缓存 null（<see cref="TrackerInstanceState.NotConfigured"/>）；
    /// 工厂初始化或迁移失败时抛 <see cref="PluginExtensionInitException"/>（不缓存，便于重试）。
    /// </summary>
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

    /// <summary>
    /// 失效某实例的所有扩展缓存（按 <paramref name="instanceId"/>，不按类型），
    /// 使下次 <see cref="GetExtension{T}"/> 重新走工厂。重试迁移失败实例前调用。
    /// </summary>
    public void InvalidateExtensions(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        var keys = _extensions.Keys.Where(k => k.InstanceId == instanceId).ToArray();
        foreach (var key in keys)
            _extensions.Remove(key);
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
        Metadata = ParseWorkTagMetadata(ReadString(r, 5)),
    };

    protected static string SerializeWorkTagMetadata(IReadOnlyDictionary<string, string> metadata) =>
        JsonSerializer.Serialize(metadata);

    protected static string SerializeTagExtraFieldOptions(IReadOnlyCollection<string> options) =>
        JsonSerializer.Serialize(options);

    protected static string[] ParseTagExtraFieldOptions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static Dictionary<string, string> ParseWorkTagMetadata(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    protected TagExtraFieldDefinition MapTagExtraFieldDefinition(DbDataReader r) => new()
    {
        FieldId = ReadString(r, 0),
        FieldKey = ReadString(r, 1),
        TagId = r.GetInt32(2),
        Label = ReadString(r, 3),
        Type = (TagExtraFieldType)r.GetInt32(4),
        Description = ReadString(r, 5),
        SortOrder = r.GetInt32(6),
        Options = ParseTagExtraFieldOptions(ReadString(r, 7)),
        Enabled = Convert.ToBoolean(r.GetValue(8)),
    };

    protected WorkItemExtraField MapWorkItemExtraField(DbDataReader r)
        => MapWorkItemExtraField(r, 0);

    protected WorkItemExtraField MapWorkItemExtraField(DbDataReader r, int offset) => new()
    {
        FieldId = ReadString(r, offset),
        FieldKey = ReadString(r, offset + 1),
        TagId = r.GetInt32(offset + 2),
        TagName = ReadString(r, offset + 3),
        Label = ReadString(r, offset + 4),
        Type = (TagExtraFieldType)r.GetInt32(offset + 5),
        Description = ReadString(r, offset + 6),
        SortOrder = r.GetInt32(offset + 7),
        Options = ParseTagExtraFieldOptions(ReadString(r, offset + 8)),
        Enabled = Convert.ToBoolean(r.GetValue(offset + 9)),
        Value = r.IsDBNull(offset + 10) ? string.Empty : ReadString(r, offset + 10),
    };

    protected WorkItem MapWorkItem(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        CreateDate = ReadString(r, 1),
        Comment = ReadString(r, 2),
        Time = r.GetFloat(3),
        Priority = (WorkPriorities)r.GetInt32(4),
        IsReadOnly = Convert.ToBoolean(r.GetValue(5)),
    };

    #endregion
}
