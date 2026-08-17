using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Diary.Core.Data.Base;
using Diary.Core.Data.Statistics;
using Diary.Database;
using Npgsql;

namespace Diary.Db.PostgreSQL;

public sealed class PgDb(IDbFactory factory) : DbInterfaceBase(factory), IDisposable, IAsyncDisposable
{
    private const int WorkTagQueryBatchSize = 500;
    private NpgsqlDataSource? _dataSource;
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;
    private Stopwatch _stopwatch = new();
    private long _lastCommandTime;

    #region provider primitives

    protected override DbCommand CreateCommand(string sql) => Command(sql);

    protected override string ReadString(DbDataReader reader, int ordinal)
        => reader.GetString(ordinal).TrimEnd();

    protected override void BindParameter(DbCommand cmd, string name, object? value)
        => ((NpgsqlCommand)cmd).Parameters.AddWithValue(value ?? DBNull.Value);

    #endregion

    public override bool Connect()
    {
        var cfg = Factory.GetConfig() as Config;
        Debug.Assert(cfg != null);
        var csb = new NpgsqlConnectionStringBuilder()
        {
            ApplicationName = "DiaryAppNG",
            Host = cfg.Host,
            Port = cfg.Port,
            Database = cfg.Database,
            Username = cfg.User,
            Password = cfg.Password,
            CommandTimeout = 5,
        };

        try
        {
            var dsb = new NpgsqlSlimDataSourceBuilder(csb.ConnectionString);
            _dataSource = dsb.Build();
            _lastCommandTime = _stopwatch.ElapsedMilliseconds;
            _stopwatch.Start();
        }
        catch (Exception)
        {
            return false;
        }

        return _dataSource != null;
    }

    #region helpers

    private NpgsqlCommand Command(string statement)
    {
        _lastCommandTime = _stopwatch.ElapsedMilliseconds;
        if (_connection != null)
        {
            var cmd = new NpgsqlCommand(statement, _connection);
            if (_transaction != null)
                cmd.Transaction = _transaction;
            return cmd;
        }

        return _dataSource!.CreateCommand(statement);
    }

    #endregion

    public override bool Initialized()
    {
        var sql = """
                  CREATE TABLE IF NOT EXISTS work_tags (
                  	id SERIAL PRIMARY KEY,
                  	tag_name CHAR(64) NOT NULL UNIQUE,
                  	tag_color INTEGER NOT NULL DEFAULT 0,
                  	tag_level INTEGER NOT NULL DEFAULT 0,
                  is_disabled INTEGER NOT NULL DEFAULT 0,
                  tag_metadata TEXT NOT NULL DEFAULT '{}'
                  );

                  CREATE TABLE IF NOT EXISTS work_items (
                  	id SERIAL PRIMARY KEY,
                  	create_date CHAR(16) NOT NULL,
                  	comment CHAR(256) NOT NULL,
                  hours REAL DEFAULT 0.0,
                  priority INTEGER DEFAULT 0,
                  is_read_only BOOLEAN NOT NULL DEFAULT FALSE
                  );

                  ALTER TABLE work_items ADD COLUMN IF NOT EXISTS is_read_only BOOLEAN NOT NULL DEFAULT FALSE;

                  CREATE TABLE IF NOT EXISTS work_notes (
                  	id INTEGER PRIMARY KEY REFERENCES work_items (id) ON DELETE CASCADE,
                  	note TEXT NOT NULL
                  );

                  CREATE TABLE IF NOT EXISTS work_item_tags (
                  	work_id INTEGER REFERENCES work_items (id) ON DELETE CASCADE,
                  	tag_id INTEGER REFERENCES work_tags (id) ON DELETE CASCADE,
                  	PRIMARY KEY (work_id, tag_id)
                  );

                  CREATE TABLE IF NOT EXISTS tag_extra_field_definitions (
                     field_id TEXT PRIMARY KEY,
                     field_key TEXT NOT NULL,
                     tag_id INTEGER NOT NULL REFERENCES work_tags(id) ON DELETE CASCADE,
                     label TEXT NOT NULL,
                     field_type INTEGER NOT NULL,
                     description TEXT NOT NULL DEFAULT '',
                     sort_order INTEGER NOT NULL DEFAULT 0,
                     options_json TEXT NOT NULL DEFAULT '[]',
                     enabled BOOLEAN NOT NULL DEFAULT TRUE
                  );
                  CREATE UNIQUE INDEX IF NOT EXISTS ux_tag_extra_fields_key
                     ON tag_extra_field_definitions (LOWER(field_key));
                  CREATE INDEX IF NOT EXISTS idx_tag_extra_fields_tag
                     ON tag_extra_field_definitions(tag_id, enabled, sort_order);
                  CREATE TABLE IF NOT EXISTS work_item_extra_field_values (
                     work_id INTEGER NOT NULL REFERENCES work_items(id) ON DELETE CASCADE,
                     field_id TEXT NOT NULL REFERENCES tag_extra_field_definitions(field_id),
                     value_json TEXT NOT NULL DEFAULT '',
                     PRIMARY KEY (work_id, field_id)
                  );
                  CREATE INDEX IF NOT EXISTS idx_work_item_extra_fields_work
                     ON work_item_extra_field_values(work_id);

                  CREATE TABLE IF NOT EXISTS data_versions (version_code INTEGER PRIMARY KEY);

                   -- default data version is 1.0.0 (0x10000 = 65536)
                  INSERT
                  	INTO data_versions
                  VALUES
                    (65536)
                  ON CONFLICT (version_code)
                  	DO NOTHING;
                  """;
        using var cmd = Command(sql);
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    public override bool KeepAlive()
    {
        if (_stopwatch.ElapsedMilliseconds - _lastCommandTime < 30_000)
        {
            return true;
        }

        using var cmd = Command("select version();");
        return cmd.ExecuteScalar() != null;
    }

    public override void Close()
    {
        _dataSource!.Dispose();
        _dataSource = null;
    }

    public override uint GetDataVersion()
    {
        using var cmd = Command("SELECT * FROM data_versions ORDER BY version_code DESC LIMIT 1;");
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToUInt32(result) : 0;
    }

    // $1=name $2=level $3=color $4=metadata
    public override WorkTag CreateWorkTag(
        string name,
        bool primary,
        int color,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var sql = """
                  INSERT INTO work_tags(tag_name, tag_level, tag_color, tag_metadata)
                  VALUES ($1, $2, $3, $4)
                  ON CONFLICT (tag_name) DO NOTHING
                  RETURNING *;
                  """;
        return QueryFirst(
            sql,
            MapWorkTag,
            ("$1", name),
            ("$2", primary ? 0 : 1),
            ("$3", color),
            ("$4", SerializeWorkTagMetadata(metadata ?? new Dictionary<string, string>())))
            ?? new WorkTag();
    }

    // $1=level $2=color $3=disabled $4=metadata $5=id
    public override bool UpdateWorkTag(WorkTag tag)
    {
        if (tag.Id == 0)
            return false;
        var sql = """
                  UPDATE work_tags
                  SET tag_level=$1, tag_color=$2, is_disabled=$3, tag_metadata=$4
                  WHERE id=$5;
                  """;
        return Execute(sql,
            ("$1", (int)tag.Level),
            ("$2", tag.Color),
            ("$3", tag.Disabled ? 1 : 0),
            ("$4", SerializeWorkTagMetadata(tag.Metadata)),
            ("$5", tag.Id)) > 0;
    }

    // $1=id
    public override bool DeleteWorkTag(WorkTag tag)
    {
        if (tag.Id == 0)
            return false;
        var sql = """
                  DELETE FROM work_tags WHERE id=$1;
                  """;
        return Execute(sql, ("$1", tag.Id)) > 0;
    }

    public override ICollection<WorkTag> AllWorkTags()
    {
        const string sql = "SELECT * FROM work_tags ORDER BY is_disabled, tag_level, id;";
        return Query(sql, MapWorkTag);
    }

    // $1=new $2=old
    public override bool UpdateWorkTagId(int oldId, int newId)
    {
        const string sql = "UPDATE work_tags SET id=$1 WHERE id=$2;";
        return Execute(sql, ("$1", newId), ("$2", oldId)) > 0;
    }

    public override ICollection<TagExtraFieldDefinition> GetTagExtraFieldDefinitions(
        int tagId, bool includeDisabled = false)
    {
        var sql = """
                  SELECT field_id, field_key, tag_id, label, field_type, description,
                         sort_order, options_json, enabled
                  FROM tag_extra_field_definitions
                  WHERE tag_id=$1
                  """ + (includeDisabled ? string.Empty : " AND enabled=TRUE") +
                  " ORDER BY sort_order, field_key;";
        return Query(sql, MapTagExtraFieldDefinition, ("$1", tagId));
    }

    public override ICollection<TagExtraFieldDefinition> GetAllTagExtraFieldDefinitions(
        bool includeDisabled = false)
    {
        var sql = """
                  SELECT field_id, field_key, tag_id, label, field_type, description,
                         sort_order, options_json, enabled
                  FROM tag_extra_field_definitions
                  """ + (includeDisabled ? string.Empty : " WHERE enabled=TRUE") +
                  " ORDER BY tag_id, sort_order, field_key;";
        return Query(sql, MapTagExtraFieldDefinition);
    }

    public override bool CreateTagExtraFieldDefinition(TagExtraFieldDefinition definition)
    {
        if (definition.TagId <= 0
            || !TagExtraFieldKeyRules.IsValid(definition.FieldKey)
            || string.IsNullOrWhiteSpace(definition.Label)
            || string.IsNullOrWhiteSpace(definition.FieldId)
            || !IsTagExtraFieldKeyAvailable(definition.FieldKey))
            return false;
        const string sql = """
                           INSERT INTO tag_extra_field_definitions
                              (field_id, field_key, tag_id, label, field_type, description,
                               sort_order, options_json, enabled)
                           VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                           ON CONFLICT DO NOTHING;
                           """;
        return Execute(sql,
            ("$1", definition.FieldId),
            ("$2", TagExtraFieldKeyRules.Normalize(definition.FieldKey)),
            ("$3", definition.TagId),
            ("$4", definition.Label.Trim()),
            ("$5", (int)definition.Type),
            ("$6", definition.Description ?? string.Empty),
            ("$7", definition.SortOrder),
            ("$8", SerializeTagExtraFieldOptions(definition.Options)),
            ("$9", definition.Enabled)) > 0;
    }

    public override bool UpdateTagExtraFieldDefinition(TagExtraFieldDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.FieldId)
            || !TagExtraFieldKeyRules.IsValid(definition.FieldKey)
            || string.IsNullOrWhiteSpace(definition.Label))
            return false;
        var current = QueryFirst(
            "SELECT field_id, field_key, tag_id, label, field_type, description, sort_order, options_json, enabled " +
            "FROM tag_extra_field_definitions WHERE field_id=$1;",
            MapTagExtraFieldDefinition,
            ("$1", definition.FieldId));
        if (current is null
            || !string.Equals(current.FieldKey, TagExtraFieldKeyRules.Normalize(definition.FieldKey), StringComparison.OrdinalIgnoreCase)
            || current.Type != definition.Type
            || current.TagId != definition.TagId)
            return false;
        const string sql = """
                           UPDATE tag_extra_field_definitions
                           SET label=$1, description=$2, sort_order=$3,
                               options_json=$4, enabled=$5
                           WHERE field_id=$6;
                           """;
        return Execute(sql,
            ("$1", definition.Label.Trim()),
            ("$2", definition.Description ?? string.Empty),
            ("$3", definition.SortOrder),
            ("$4", SerializeTagExtraFieldOptions(definition.Options)),
            ("$5", definition.Enabled),
            ("$6", definition.FieldId)) > 0;
    }

    public override bool IsTagExtraFieldKeyAvailable(string fieldKey, string? excludingFieldId = null)
    {
        if (!TagExtraFieldKeyRules.IsValid(fieldKey))
            return false;
        var sql = "SELECT 1 FROM tag_extra_field_definitions WHERE lower(field_key)=lower($1)";
        if (!string.IsNullOrWhiteSpace(excludingFieldId))
            sql += " AND field_id<>$2";
        sql += ";";
        var args = new List<(string Name, object? Value)>
        {
            ("$1", TagExtraFieldKeyRules.Normalize(fieldKey))
        };
        if (!string.IsNullOrWhiteSpace(excludingFieldId))
            args.Add(("$2", excludingFieldId));
        return !Exists(sql, args.ToArray());
    }

    public override ICollection<WorkItemExtraField> GetWorkItemExtraFields(WorkItem item)
    {
        if (item.Id <= 0)
            return Array.Empty<WorkItemExtraField>();
        const string sql = """
                           SELECT d.field_id, d.field_key, d.tag_id, t.tag_name, d.label,
                                  d.field_type, d.description, d.sort_order, d.options_json,
                                  d.enabled, v.value_json
                           FROM work_item_tags wit
                           INNER JOIN tag_extra_field_definitions d
                              ON d.tag_id=wit.tag_id AND d.enabled=TRUE
                           INNER JOIN work_tags t ON t.id=d.tag_id
                           LEFT JOIN work_item_extra_field_values v
                              ON v.work_id=wit.work_id AND v.field_id=d.field_id
                           WHERE wit.work_id=$1
                           ORDER BY t.tag_level, t.id, d.sort_order, d.field_key;
                           """;
        return Query(sql, MapWorkItemExtraField, ("$1", item.Id));
    }

    public override bool SaveWorkItemExtraFieldValues(
        int workItemId, IReadOnlyCollection<WorkItemExtraFieldValue> values)
    {
        if (workItemId <= 0 || !IsWorkItemWritable(workItemId))
            return false;
        var ownsTransaction = _transaction is null;
        if (ownsTransaction && !BeginTransaction())
            return false;
        try
        {
            Execute("""
                    DELETE FROM work_item_extra_field_values
                    WHERE work_id=$1
                      AND field_id IN (
                          SELECT d.field_id
                          FROM work_item_tags wit
                          INNER JOIN tag_extra_field_definitions d ON d.tag_id=wit.tag_id
                          WHERE wit.work_id=$1 AND d.enabled=TRUE);
                    """, ("$1", workItemId));
            foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value.FieldId)
                                                         && !string.IsNullOrWhiteSpace(value.Value)))
            {
                Execute("""
                        INSERT INTO work_item_extra_field_values(work_id, field_id, value_json)
                        SELECT $1, d.field_id, $2
                        FROM tag_extra_field_definitions d
                        INNER JOIN work_item_tags wit ON wit.tag_id=d.tag_id
                        WHERE wit.work_id=$1 AND d.field_id=$3 AND d.enabled=TRUE
                        ON CONFLICT (work_id, field_id)
                        DO UPDATE SET value_json=EXCLUDED.value_json;
                        """,
                    ("$1", workItemId),
                    ("$2", value.Value),
                    ("$3", value.FieldId));
            }
            if (ownsTransaction && !CommitTransaction())
                return false;
            return true;
        }
        catch (Exception)
        {
            if (ownsTransaction)
                RollbackTransaction();
            return false;
        }
    }

    // $1=date $2=comment
    public override WorkItem CreateWorkItem(string date, string comment)
    {
        var sql = """
                  INSERT INTO work_items(create_date, comment) VALUES ($1, $2) returning *;
                  """;
        return QueryFirst(sql, MapWorkItem, ("$1", date), ("$2", comment)) ?? new WorkItem();
    }

    // $1=date $2=comment $3=time $4=priority $5=id
    public override bool UpdateWorkItem(WorkItem item)
    {
        if (item.Id == 0)
            return false;
        var sql = """
                  UPDATE work_items SET create_date=$1, comment=$2, hours=$3, priority=$4  WHERE id=$5 AND is_read_only=FALSE;
                  """;
        return Execute(sql,
            ("$1", item.CreateDate), ("$2", item.Comment),
            ("$3", item.Time), ("$4", (int)item.Priority), ("$5", item.Id)) > 0;
    }

    // $1=id
    public override bool DeleteWorkItem(WorkItem item)
    {
        if (item.Id == 0)
            return false;
        var sql = """
                  DELETE FROM work_items WHERE id=$1;
                  """;
        return Execute(sql, ("$1", item.Id)) > 0;
    }

    // $1=begin $2=end
    public override ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData)
    {
        var sql = """
                  SELECT *
                  FROM work_items
                  WHERE create_date BETWEEN $1 AND $2
                  ORDER BY priority;
                  """;
        return Query(sql, MapWorkItem, ("$1", beginData), ("$2", endData));
    }

    // $1=date
    public override ICollection<WorkItem> GetWorkItemByDate(string date)
    {
        const string sql = @"SELECT * FROM work_items WHERE create_date=$1 ORDER BY priority;";
        return Query(sql, MapWorkItem, ("$1", date));
    }

    // $1=new $2=old
    public override bool UpdateWorkItemId(int oldId, int newId)
    {
        const string sql = "UPDATE work_items SET id=$1 WHERE id=$2 AND is_read_only=FALSE;";
        return Execute(sql, ("$1", newId), ("$2", oldId)) > 0;
    }

    public override bool MarkWorkItemReadOnly(WorkItem item)
    {
        if (item.Id == 0)
            return false;
        const string sql = "UPDATE work_items SET is_read_only=TRUE WHERE id=$1;";
        var updated = Execute(sql, ("$1", item.Id)) > 0;
        if (updated)
            item.IsReadOnly = true;
        return updated;
    }

    // $1=id $2=note
    public override void WorkUpdateNote(WorkItem work, string content)
    {
        if (work.Id == 0)
            throw new ArgumentNullException(nameof(work.Id));
        if (!IsWorkItemWritable(work.Id))
            throw new InvalidOperationException("只读工作项不可修改备注");
        var sql = """
                  INSERT INTO work_notes(id, note) VALUES ($1, $2) ON CONFLICT(id) DO UPDATE SET note=$2;
                  """;
        Execute(sql, ("$1", work.Id), ("$2", content));
    }

    // $1=id
    public override void WorkDeleteNote(WorkItem work)
    {
        if (work.Id == 0)
            throw new ArgumentNullException(nameof(work.Id));
        if (!IsWorkItemWritable(work.Id))
            throw new InvalidOperationException("只读工作项不可删除备注");
        var sql = """
                  DELETE FROM work_notes WHERE id=$1;
                  """;
        Execute(sql, ("$1", work.Id));
    }

    // $1=id
    public override string? WorkGetNote(WorkItem work)
    {
        if (work.Id == 0)
            throw new ArgumentNullException(nameof(work.Id));
        var sql = """
                  SELECT note FROM work_notes WHERE id=$1;
                  """;
        return QueryFirst(sql, r => ReadString(r, 0), ("$1", work.Id));
    }

    public override Dictionary<int, string> GetWorkNotesByDate(string date)
    {
        var sql = """
                  SELECT work_notes.id, work_notes.note
                  FROM work_notes INNER JOIN work_items ON work_notes.id = work_items.id
                  WHERE work_items.create_date = $1;
                  """;
        var rows = Query<(int Id, string Note)>(
            sql, r => (r.GetInt32(0), ReadString(r, 1)), ("$1", date));
        var result = new Dictionary<int, string>();
        foreach (var (id, note) in rows)
            result[id] = note;
        return result;
    }

    public override Dictionary<int, string> GetWorkNotesByWorkItemIds(
        IReadOnlyCollection<int> workItemIds)
    {
        ArgumentNullException.ThrowIfNull(workItemIds);
        var ids = workItemIds.Where(id => id != 0).Distinct().ToArray();
        var result = new Dictionary<int, string>();
        foreach (var batch in ids.Chunk(WorkTagQueryBatchSize))
        {
            var placeholders = new string[batch.Length];
            var args = new (string Name, object? Value)[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                placeholders[i] = $"${i + 1}";
                args[i] = (placeholders[i], batch[i]);
            }
            var sql = $"SELECT id, note FROM work_notes WHERE id IN ({string.Join(", ", placeholders)});";
            foreach (var (id, note) in Query<(int Id, string Note)>(
                         sql, r => (r.GetInt32(0), ReadString(r, 1)), args))
                result[id] = note;
        }
        return result;
    }

    public override Dictionary<int, ICollection<WorkTag>> GetWorkTagsByDate(string date)
    {
        var sql = """
                  SELECT work_tags.*, work_item_tags.work_id
                  FROM work_item_tags
                  INNER JOIN work_tags ON work_item_tags.tag_id = work_tags.id
                  INNER JOIN work_items ON work_item_tags.work_id = work_items.id
                  WHERE work_items.create_date = $1
                  ORDER BY work_tags.tag_level;
                  """;
        var rows = Query<(WorkTag Tag, int WorkId)>(
            sql, r => (MapWorkTag(r), r.GetInt32(6)), ("$1", date));
        var result = new Dictionary<int, ICollection<WorkTag>>();
        foreach (var (tag, workId) in rows)
        {
            if (!result.TryGetValue(workId, out var list))
            {
                list = new List<WorkTag>();
                result[workId] = list;
            }
            list.Add(tag);
        }
        return result;
    }

    public override Dictionary<int, ICollection<WorkTag>> GetWorkTagsByWorkItemIds(
        IReadOnlyCollection<int> workItemIds)
    {
        ArgumentNullException.ThrowIfNull(workItemIds);
        var ids = workItemIds.Where(id => id != 0).Distinct().ToArray();
        if (ids.Length == 0)
            return new Dictionary<int, ICollection<WorkTag>>();

        var result = new Dictionary<int, ICollection<WorkTag>>();
        foreach (var batch in ids.Chunk(WorkTagQueryBatchSize))
        {
            var args = new (string Name, object? Value)[batch.Length];
            var placeholders = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                placeholders[i] = $"${i + 1}";
                args[i] = (placeholders[i], batch[i]);
            }
            var sql = $"""
                       SELECT work_tags.*, work_item_tags.work_id
                       FROM work_item_tags INNER JOIN work_tags ON work_item_tags.tag_id = work_tags.id
                       WHERE work_item_tags.work_id IN ({string.Join(", ", placeholders)})
                       ORDER BY work_item_tags.work_id, work_tags.tag_level, work_tags.id;
                       """;
            var rows = Query<(WorkTag Tag, int WorkId)>(
                sql, r => (MapWorkTag(r), r.GetInt32(6)), args);
            AddGroupedWorkTags(result, rows);
        }
        return result;
    }

    private static void AddGroupedWorkTags(
        Dictionary<int, ICollection<WorkTag>> result,
        IEnumerable<(WorkTag Tag, int WorkId)> rows)
    {
        foreach (var (tag, workId) in rows)
        {
            if (!result.TryGetValue(workId, out var tags))
            {
                tags = new List<WorkTag>();
                result[workId] = tags;
            }
            tags.Add(tag);
        }
    }

    // $1=work_id $2=tag_id
    public override bool WorkItemAddTag(WorkItem item, WorkTag tag)
    {
        if (item.Id == 0 || tag.Id == 0)
            throw new ArgumentException($"{nameof(item.Id)} or {nameof(tag.Id)} is required");
        if (!IsWorkItemWritable(item.Id))
            return false;
        try
        {
            var sql = """
                      INSERT INTO work_item_tags(work_id, tag_id) VALUES ($1, $2);
                      """;
            return Execute(sql, ("$1", item.Id), ("$2", tag.Id)) > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // $1=work_id $2=tag_id
    public override bool WorkItemRemoveTag(WorkItem item, WorkTag tag)
    {
        if (item.Id == 0 || tag.Id == 0)
            throw new ArgumentException($"{nameof(item.Id)} or {nameof(tag.Id)} is required");
        if (!IsWorkItemWritable(item.Id))
            return false;
        var sql = """
                  DELETE FROM work_item_tags WHERE work_id=$1 AND tag_id=$2;
                  """;
        return Execute(sql, ("$1", item.Id), ("$2", tag.Id)) > 0;
    }

    // $1=work_id
    public override bool WorkItemCleanTags(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentNullException(nameof(item.Id));
        if (!IsWorkItemWritable(item.Id))
            return false;
        var sql = """
                  DELETE FROM work_item_tags WHERE work_id=$1;
                  """;
        return Execute(sql, ("$1", item.Id)) > 0;
    }

    private bool IsWorkItemWritable(int id)
        => Exists("SELECT 1 FROM work_items WHERE id=$1 AND is_read_only=FALSE;", ("$1", id));

    // $1=work_id
    public override ICollection<WorkTag> GetWorkItemTags(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentNullException(nameof(item.Id));
        var sql = """
                  SELECT work_tags.*
                  FROM work_item_tags INNER JOIN work_tags ON work_item_tags.tag_id=work_tags.id
                  WHERE work_item_tags.work_id = $1
                  ORDER BY work_tags.tag_level;
                  """;
        return Query(sql, MapWorkTag, ("$1", item.Id));
    }

    public override StatisticsResult GetStatistics(string beginDate, string endDate)
    {
        var result = new StatisticsResult()
        {
            DateBegin = beginDate,
            DateEnd = endDate,
            PrimaryTags = new List<TagTime>(),
        };

        // total time
        {
            var dateRangeQuery = "SELECT sum(hours) FROM work_items WHERE create_date BETWEEN $1 AND $2;";
            using var cmd = Command(dateRangeQuery);
            cmd.Parameters.AddWithValue(beginDate);
            cmd.Parameters.AddWithValue(endDate);
            using var reader = cmd.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0))
                result.Total = reader.GetFloat(0);
        }

        if (result.Total > 0)
        {
            var sql = """
                      SELECT work_tags.id AS tid, sum(hours) AS total, tag_name 
                      FROM 
                      	((work_item_tags INNER JOIN
                      			(SELECT id,hours FROM work_items WHERE create_date BETWEEN $1 AND $2) AS T1
                      		ON work_item_tags.work_id=T1.id) AS T2
                      	INNER JOIN work_tags ON work_tags.id=T2.tag_id AND work_tags.tag_level=0)
                      GROUP BY tid;
                      """;

            // 一级标签
            using var cmd = Command(sql);
            cmd.Parameters.AddWithValue(beginDate);
            cmd.Parameters.AddWithValue(endDate);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.PrimaryTags.Add(new TagTime()
                {
                    TagId = reader.GetInt32(0),
                    Time = reader.GetFloat(1),
                    TagName = ReadString(reader, 2),
                    Nested = new List<TagTime>(),
                });
            }
        }

        if (result.PrimaryTags.Count > 0)
        {
            var nestedSql = """
                            SELECT
                            	primary_tags.tag_id AS primary_id,
                            	work_tags.id,
                            	SUM(T1.hours) AS total,
                            	work_tags.tag_name
                            FROM
                            	(SELECT wit.work_id, wit.tag_id
                            	 FROM work_item_tags wit
                            	 INNER JOIN work_tags ON work_tags.id = wit.tag_id AND work_tags.tag_level = 0) AS primary_tags
                            	INNER JOIN work_item_tags AS nested_tags
                            		ON primary_tags.work_id = nested_tags.work_id AND nested_tags.tag_id != primary_tags.tag_id
                            	INNER JOIN work_tags ON work_tags.id = nested_tags.tag_id AND work_tags.tag_level != 0
                            	INNER JOIN (SELECT id, hours FROM work_items WHERE create_date BETWEEN $1 AND $2) AS T1
                            		ON primary_tags.work_id = T1.id
                            GROUP BY primary_tags.tag_id, work_tags.id, work_tags.tag_name;
                            """;

            using var nestedCmd = Command(nestedSql);
            nestedCmd.Parameters.AddWithValue(beginDate);
            nestedCmd.Parameters.AddWithValue(endDate);
            using var nestedReader = nestedCmd.ExecuteReader();

            var nestedMap = result.PrimaryTags.ToDictionary(t => t.TagId);
            while (nestedReader.Read())
            {
                var primaryId = nestedReader.GetInt32(0);
                if (nestedMap.TryGetValue(primaryId, out var primaryTag))
                {
                    primaryTag.Nested.Add(new TagTime()
                    {
                        TagId = nestedReader.GetInt32(1),
                        Time = nestedReader.GetFloat(2),
                        TagName = ReadString(nestedReader, 3),
                    });
                }
            }
        }

        return result;
    }

    public override ICollection<WorkItem> GetWorkItemsByTagAndDate(string dateBegin, string dateEnd, int l1, int l2 = 0)
    {
        if (l2 == 0)
        {
            // $1=tag_id $2=begin $3=end
            var sql = """
                      SELECT work_items.* FROM
                      (work_items INNER JOIN work_item_tags on work_items.id = work_item_tags.work_id)
                      WHERE work_item_tags.tag_id = $1 AND work_items.create_date BETWEEN $2 AND $3
                      ORDER BY create_date, priority, id;
                      """;
            return Query(sql, MapWorkItem, ("$1", l1), ("$2", dateBegin), ("$3", dateEnd));
        }

        {
            // $1=begin $2=end $3=primary $4=secondary
            var sql = """
                      SELECT work_items.* FROM
                      work_items INNER JOIN
                      (SELECT work_item_tags.work_id FROM
                      	(SELECT work_id FROM work_item_tags WHERE tag_id=$3) AS T0
                      	INNER JOIN work_item_tags ON T0.work_id = work_item_tags.work_id AND work_item_tags.tag_id=$4) AS T1
                      	ON work_items.id=T1.work_id WHERE create_date BETWEEN $1 AND $2
                      ORDER BY create_date, priority, id;
                      """;
            return Query(sql, MapWorkItem,
                ("$1", dateBegin), ("$2", dateEnd), ("$3", l1), ("$4", l2));
        }
    }

    public override ICollection<WorkItem> QueryWorkItems(WorkItemQuery query)
    {
        query = WorkItemQueryNormalizer.Normalize(query);
        var tagIds = query.TagIds.Distinct().ToArray();
        if (query.TagFilter is WorkItemTagFilter.Any or WorkItemTagFilter.All && tagIds.Length == 0)
            return Array.Empty<WorkItem>();

        var sql = new StringBuilder("SELECT work_items.* FROM work_items WHERE 1=1");
        var args = new List<(string Name, object? Value)>();
        string AddParameter(object value)
        {
            var placeholder = $"${args.Count + 1}";
            args.Add((placeholder, value));
            return placeholder;
        }

        if (!string.IsNullOrWhiteSpace(query.StartDate))
            sql.Append(" AND work_items.create_date >= ").Append(AddParameter(query.StartDate));
        if (!string.IsNullOrWhiteSpace(query.EndDate))
            sql.Append(" AND work_items.create_date <= ").Append(AddParameter(query.EndDate));
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var text = AddParameter(query.Text);
            sql.Append(" AND (strpos(lower(work_items.comment), lower(").Append(text)
                .Append(")) > 0 OR EXISTS (SELECT 1 FROM work_notes wn WHERE wn.id = work_items.id AND strpos(lower(wn.note), lower(")
                .Append(text).Append(")) > 0))");
        }
        if (query.Priority is not null)
            sql.Append(" AND work_items.priority = ").Append(AddParameter((int)query.Priority.Value));

        if (query.TagFilter == WorkItemTagFilter.None
            || query.TagFilter == WorkItemTagFilter.Exact && tagIds.Length == 0)
        {
            sql.Append(" AND NOT EXISTS (SELECT 1 FROM work_item_tags wit WHERE wit.work_id = work_items.id)");
        }
        else if (query.TagFilter is WorkItemTagFilter.Any or WorkItemTagFilter.All or WorkItemTagFilter.Exact)
        {
            var placeholders = tagIds.Select(tagId => AddParameter(tagId)).ToArray();
            sql.Append(" AND (SELECT COUNT(DISTINCT wit.tag_id) FROM work_item_tags wit WHERE wit.work_id = work_items.id AND wit.tag_id IN (")
                .AppendJoin(", ", placeholders)
                .Append("))");
            sql.Append(query.TagFilter == WorkItemTagFilter.Any ? " > 0" : $" = {tagIds.Length}");
            if (query.TagFilter == WorkItemTagFilter.Exact)
                sql.Append($" AND (SELECT COUNT(*) FROM work_item_tags all_tags WHERE all_tags.work_id = work_items.id) = {tagIds.Length}");
        }

        sql.Append(" ORDER BY work_items.create_date, work_items.id");
        if (query.Limit is > 0)
        {
            sql.Append(" LIMIT ").Append(AddParameter(query.Limit.Value));
            sql.Append(" OFFSET ").Append(AddParameter(Math.Max(0, query.Offset)));
        }
        return Query(sql.ToString(), MapWorkItem, args.ToArray());
    }

    public override bool DropData()
    {
        try
        {
            const string sql = """
                               DELETE FROM work_item_tags;
                               DELETE FROM work_tags;
                               DELETE FROM work_notes;
                               DELETE FROM work_items;
                               """;
            using var cmd = Command(sql);
            cmd.ExecuteNonQuery();
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    public override bool BeginTransaction()
    {
        Debug.Assert(_connection == null);

        // 使用同一个 connection 去操作数据库
        _connection = _dataSource!.OpenConnection();
        _transaction = _connection.BeginTransaction();
        return true;
    }

    public override bool CommitTransaction()
    {
        Debug.Assert(_connection != null);

        if (_transaction != null)
        {
            _transaction.Commit();
            _transaction.Dispose();
            _transaction = null;
        }
        _connection.Dispose();
        _connection = null;
        return true;
    }

    public override bool RollbackTransaction()
    {
        Debug.Assert(_connection != null);

        if (_transaction != null)
        {
            _transaction.Rollback();
            _transaction.Dispose();
            _transaction = null;
        }
        _connection.Dispose();
        _connection = null;
        return true;
    }

    public override void Dispose()
    {
        _transaction?.Dispose();
        _transaction = null;
        _connection?.Dispose();
        _connection = null;
        _dataSource?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction != null)
            await _transaction.DisposeAsync();
        _transaction = null;
        if (_connection != null)
            await _connection.DisposeAsync();
        _connection = null;
        if (_dataSource != null)
            await _dataSource.DisposeAsync();
    }
}
