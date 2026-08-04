using System.Data.Common;
using System.Diagnostics;
using Diary.Core.Data.Base;
using Diary.Core.Data.Statistics;
using Diary.Database;
using Diary.RedMine;
using Diary.RedMine.Models;
using Npgsql;

namespace Diary.Db.PostgreSQL;

public sealed class PgDb(IDbFactory factory) : DbInterfaceBase(factory), IDisposable, IAsyncDisposable
{
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
                  	is_disabled INTEGER NOT NULL DEFAULT 0
                  );

                  CREATE TABLE IF NOT EXISTS work_items (
                  	id SERIAL PRIMARY KEY,
                  	create_date CHAR(16) NOT NULL,
                  	comment CHAR(256) NOT NULL,
                  	hours REAL DEFAULT 0.0,
                  	priority INTEGER DEFAULT 0
                  );

                  CREATE TABLE IF NOT EXISTS work_notes (
                  	id INTEGER PRIMARY KEY REFERENCES work_items (id) ON DELETE CASCADE,
                  	note TEXT NOT NULL
                  );

                  CREATE TABLE IF NOT EXISTS work_item_tags (
                  	work_id INTEGER REFERENCES work_items (id) ON DELETE CASCADE,
                  	tag_id INTEGER REFERENCES work_tags (id) ON DELETE CASCADE,
                  	PRIMARY KEY (work_id, tag_id)
                  );

                  CREATE TABLE IF NOT EXISTS redmine_projects (
                  	id INTEGER NOT NULL PRIMARY KEY,
                  	project_name CHAR(256) NOT NULL,
                  	project_desc CHAR(2048) DEFAULT '',
                  	is_closed INTEGER DEFAULT 0
                  );

                  CREATE TABLE IF NOT EXISTS redmine_activities (
                  	id INTEGER PRIMARY KEY,
                  	act_name CHAR(64) NOT NULL
                  );

                  CREATE TABLE IF NOT EXISTS redmine_issues (
                  	id INTEGER PRIMARY KEY,
                  	issue_title CHAR(256) NOT NULL,
                  	assigned_to CHAR(16) DEFAULT '',
                  	project_id INTEGER NOT NULL REFERENCES redmine_projects (id) ON DELETE CASCADE,
                  	is_closed INTEGER DEFAULT 0
                  );

                  CREATE TABLE IF NOT EXISTS redmine_time_entries (
                  	work_id INTEGER PRIMARY KEY REFERENCES work_items (id) ON DELETE CASCADE,
                  	id INTEGER DEFAULT 0,
                  	act_id INTEGER REFERENCES redmine_activities (id) ON DELETE CASCADE,
                  	issue_id INTEGER REFERENCES redmine_issues (id) ON DELETE CASCADE
                  );

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

    // $1=name $2=level $3=color
    public override WorkTag CreateWorkTag(string name, bool primary, int color)
    {
        var sql = """
                  INSERT INTO work_tags(tag_name, tag_level, tag_color) values ($1, $2, $3) ON CONFLICT (tag_name) DO NOTHING returning *;
                  """;
        return QueryFirst(sql, MapWorkTag, ("$1", name), ("$2", primary ? 0 : 1), ("$3", color)) ?? new WorkTag();
    }

    // $1=level $2=color $3=disabled $4=id
    public override bool UpdateWorkTag(WorkTag tag)
    {
        if (tag.Id == 0)
            return false;
        var sql = """
                  UPDATE work_tags SET tag_level=$1, tag_color=$2, is_disabled=$3
                  WHERE id=$4;
                  """;
        return Execute(sql,
            ("$1", (int)tag.Level), ("$2", tag.Color),
            ("$3", tag.Disabled ? 1 : 0), ("$4", tag.Id)) > 0;
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
                  UPDATE work_items SET create_date=$1, comment=$2, hours=$3, priority=$4  WHERE id=$5;
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
        const string sql = "UPDATE work_items SET id=$1 WHERE id=$2;";
        return Execute(sql, ("$1", newId), ("$2", oldId)) > 0;
    }

    // $1=id $2=note
    public override void WorkUpdateNote(WorkItem work, string content)
    {
        if (work.Id == 0)
            throw new ArgumentNullException(nameof(work.Id));
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
            sql, r => (MapWorkTag(r), r.GetInt32(5)), ("$1", date));
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

    // $1=work_id $2=tag_id
    public override bool WorkItemAddTag(WorkItem item, WorkTag tag)
    {
        if (item.Id == 0 || tag.Id == 0)
            throw new ArgumentException($"{nameof(item.Id)} or {nameof(tag.Id)} is required");
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
        var sql = """
                  DELETE FROM work_item_tags WHERE work_id=$1;
                  """;
        return Execute(sql, ("$1", item.Id)) > 0;
    }

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

    protected override object? CreateExtension(Type extensionType)
        => extensionType == typeof(IRedMineDb) ? new PgRedMineDb(this) : null;

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

    public override bool DropData()
    {
        try
        {
            using var batch = _dataSource!.CreateBatch();
            batch.BatchCommands.Add(new NpgsqlBatchCommand("DELETE FROM work_item_tags;"));
            batch.BatchCommands.Add(new NpgsqlBatchCommand("DELETE FROM work_tags;"));
            batch.BatchCommands.Add(new NpgsqlBatchCommand("DELETE FROM work_notes;"));
            batch.BatchCommands.Add(new NpgsqlBatchCommand("DELETE FROM redmine_time_entries;"));
            batch.BatchCommands.Add(new NpgsqlBatchCommand("DELETE FROM redmine_activities;"));
            batch.BatchCommands.Add(new NpgsqlBatchCommand("DELETE FROM redmine_issues;"));
            batch.BatchCommands.Add(new NpgsqlBatchCommand("DELETE FROM redmine_projects;"));
            batch.BatchCommands.Add(new NpgsqlBatchCommand("DELETE FROM work_items;"));
            batch.ExecuteNonQuery();
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
