using System.Data.Common;
using System.Data.SQLite;
using System.Diagnostics;
using Diary.Core.Data.Base;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;
using Diary.Core.Data.Statistics;
using Diary.Database;
using Diary.Utils;

namespace Diary.Db.SQLite;

public sealed class SQLiteDb(IDbFactory factory) : DbInterfaceBase(factory), IDisposable, IAsyncDisposable
{
    private SQLiteConnection? _connection;
    private SQLiteTransaction? _transaction;

    #region provider primitives

    protected override DbCommand CreateCommand(string sql)
    {
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    protected override string ReadString(DbDataReader reader, int ordinal) => reader.GetString(ordinal);

    protected override void BindParameter(DbCommand cmd, string name, object? value)
        => ((SQLiteCommand)cmd).Parameters.AddWithValue(name, value ?? DBNull.Value);

    #endregion

    public override bool Connect()
    {
        var cfg = Factory.GetConfig() as Config;
        Debug.Assert(cfg != null);
        var csb = new SQLiteConnectionStringBuilder
        {
            DataSource = cfg.FilePath,
        };
        _connection = new SQLiteConnection(csb.ToString());
        _connection.Open();

        // query version
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "select sqlite_version();";
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var version = reader.GetString(0);
            return !string.IsNullOrWhiteSpace(version);
        }

        return false;
    }

    public override bool Initialized()
    {
        const string tableInitCmd = """
                                    CREATE TABLE IF NOT EXISTS
                                    	work_tags(
                                    		id INTEGER PRIMARY KEY AUTOINCREMENT,
                                    		tag_name CHAR(64) NOT NULL UNIQUE,
                                    		tag_color INTEGER NOT NULL DEFAULT 0,
                                    		tag_level INTEGER NOT NULL DEFAULT 0,
                                    		is_disabled INTEGER NOT NULL DEFAULT 0
                                    	);
                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	work_items(
                                    		id INTEGER PRIMARY KEY AUTOINCREMENT,
                                    		create_date CHAR(16) NOT NULL,
                                    		comment CHAR(256) NOT NULL,
                                    		hours REAL DEFAULT 0.0,
                                    		priority INTEGER DEFAULT 0
                                    	);

                                    CREATE TABLE IF NOT EXISTS
                                    	work_notes(
                                    		id INTEGER PRIMARY KEY
                                    			REFERENCES work_items(id)
                                    			    ON DELETE CASCADE,
                                    		note TEXT NOT NULL
                                    	);

                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	work_item_tags(
                                    		work_id INTEGER REFERENCES work_items(id)
                                                ON DELETE CASCADE,
                                    		tag_id INTEGER REFERENCES work_tags(id)
                                                ON DELETE CASCADE,
                                    		PRIMARY KEY (work_id,tag_id)
                                    	);
                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	redmine_projects(
                                    		id INTEGER NOT NULL PRIMARY KEY,
                                    		project_name CHAR(256) NOT NULL,
                                    		project_desc CHAR(2048) DEFAULT '',
                                    		is_closed INTEGER DEFAULT 0
                                    	);
                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	redmine_activities(
                                    		id INTEGER PRIMARY KEY,
                                    		act_name CHAR(64) NOT NULL
                                    	);
                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	redmine_issues(
                                    		id INTEGER PRIMARY KEY,
                                    		issue_title CHAR(256) NOT NULL,
                                    		assigned_to CHAR(16) DEFAULT '',
                                    		project_id INTEGER NOT NULL REFERENCES
                                    			redmine_projects(id) ON DELETE CASCADE,
                                    		is_closed INTEGER default 0
                                    	);
                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	redmine_time_entries(
                                    		work_id INTEGER PRIMARY KEY
                                    			REFERENCES work_items(id)
                                                    ON DELETE CASCADE,
                                    		id INTEGER DEFAULT 0,
                                    		act_id INTEGER
                                    			REFERENCES redmine_activities(id)
                                                    ON DELETE CASCADE,
                                    		issue_id INTEGER
                                    			REFERENCES redmine_issues(id)
                                                    ON DELETE CASCADE
                                    	);

                                    CREATE TABLE IF NOT EXISTS
                                    	data_versions(
                                    		version_code INTEGER PRIMARY KEY
                                    	);

                                    -- default data version is 1.0.0 (0x1000000)
                                    INSERT OR IGNORE INTO data_versions VALUES(0x10000);
                                    
                                    CREATE INDEX IF NOT EXISTS idx_work_items_date ON work_items(create_date);
                                    CREATE INDEX IF NOT EXISTS idx_work_item_tags_tag ON work_item_tags(tag_id);
                                    CREATE INDEX IF NOT EXISTS idx_work_item_tags_work ON work_item_tags(work_id);
                                    CREATE INDEX IF NOT EXISTS idx_redmine_issues_project ON redmine_issues(project_id);
                                    """;
        using var transaction = _connection!.BeginTransaction();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = tableInitCmd;
            cmd.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (SQLiteException)
        {
            transaction.Rollback();
            return false;
        }

        return true;
    }

    public override bool KeepAlive()
    {
        return true;
    }

    public override void Close()
    {
        _connection!.Close();
        _connection = null;
    }

    public override uint GetDataVersion()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM data_versions ORDER BY version_code DESC LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return (uint)reader.GetInt32(0);
        return 0;
    }

    public override WorkTag CreateWorkTag(string name, bool primary, int color)
    {
        const string sql =
            @"INSERT OR IGNORE INTO work_tags(tag_name,tag_level,tag_color) VALUES ($value,$level,$color) RETURNING *;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$value", name);
        cmd.Parameters.AddWithValue("$level", primary ? 0 : 1);
        cmd.Parameters.AddWithValue("$color", color);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return MapWorkTag(reader);
        }

        return new WorkTag();
    }

    public override bool UpdateWorkTag(WorkTag tag)
    {
        if (tag.Id == 0)
        {
            return false;
        }

        const string sql =
            @"UPDATE OR FAIL work_tags SET tag_color=$color, tag_level=$level, is_disabled=$disabled WHERE id=$id;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$color", tag.Color);
        cmd.Parameters.AddWithValue("$level", tag.Level);
        cmd.Parameters.AddWithValue("$disabled", tag.Disabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", tag.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override bool DeleteWorkTag(WorkTag tag)
    {
        const string sql = @"DELETE FROM work_tags WHERE id=$id;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", tag.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override ICollection<WorkTag> AllWorkTags()
    {
        List<WorkTag> result = new();

        const string sql = @"SELECT * FROM work_tags ORDER BY is_disabled ASC, tag_level ASC;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(MapWorkTag(reader));
        }

        return result;
    }

    public override bool UpdateWorkTagId(int oldId, int newId)
    {
        const string sql = "UPDATE work_tags SET id=$new WHERE id=$old;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$old", oldId);
        cmd.Parameters.AddWithValue("$new", newId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override WorkItem CreateWorkItem(string date, string comment)
    {
        const string sql =
            @"INSERT INTO work_items(create_date, comment) VALUES ($create_date, $comment) RETURNING *;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$create_date", date);
        cmd.Parameters.AddWithValue("$comment", comment);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return MapWorkItem(reader);
        }

        return new WorkItem();
    }

    public override bool UpdateWorkItem(WorkItem item)
    {
        if (item.Id == 0)
            return false;

        const string sql =
            @"UPDATE work_items SET create_date=$create_date, comment=$comment, hours=$time, priority=$priority WHERE id=$id;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$create_date", item.CreateDate);
        cmd.Parameters.AddWithValue("$comment", item.Comment);
        cmd.Parameters.AddWithValue("$time", item.Time);
        cmd.Parameters.AddWithValue("$priority", (int)item.Priority);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override bool DeleteWorkItem(WorkItem item)
    {
        if (item.Id == 0)
            return false;
        const string sql =
            @"DELETE FROM work_items WHERE id=$id;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", item.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData)
    {
        var sql = """
                  SELECT *
                  FROM work_items
                  WHERE create_date BETWEEN $beginDate AND $endDate;
                  """;
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$beginDate", beginData);
        cmd.Parameters.AddWithValue("$endDate", endData);
        using var reader = cmd.ExecuteReader();
        var result = new List<WorkItem>();
        while (reader.Read())
        {
            result.Add(MapWorkItem(reader));
        }

        return result;
    }

    public override ICollection<WorkItem> GetWorkItemByDate(string date)
    {
        const string sql = @"SELECT * FROM work_items WHERE create_date=$date ORDER BY priority ASC;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$date", date);
        using var reader = cmd.ExecuteReader();
        List<WorkItem> items = new();
        while (reader.Read())
        {
            items.Add(MapWorkItem(reader));
        }

        return items;
    }

    public override bool UpdateWorkItemId(int oldId, int newId)
    {
        const string sql = "UPDATE work_items SET id=$new WHERE id=$old;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$old", oldId);
        cmd.Parameters.AddWithValue("$new", newId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override void WorkUpdateNote(WorkItem work, string content)
    {
        if (work.Id == 0)
            throw new ArgumentException("work id is required");

        const string sql =
            @"INSERT INTO work_notes(id, note) VALUES ($id, $note) ON CONFLICT (id) DO UPDATE SET note=$note RETURNING *;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", work.Id);
        cmd.Parameters.AddWithValue("$note", content);
        cmd.ExecuteNonQuery();
    }

    public override void WorkDeleteNote(WorkItem work)
    {
        const string sql =
            @"DELETE FROM work_notes WHERE id=$id;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", work.Id);
        cmd.ExecuteNonQuery();
    }

    public override string? WorkGetNote(WorkItem work)
    {
        const string sql =
            @"SELECT note FROM work_notes WHERE id=$id;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", work.Id);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return reader.GetString(0);
        }

        return null;
    }

    public override Dictionary<int, string> GetWorkNotesByDate(string date)
    {
        var sql = """
                  SELECT work_notes.id, work_notes.note
                  FROM work_notes INNER JOIN work_items ON work_notes.id = work_items.id
                  WHERE work_items.create_date = $date;
                  """;
        var rows = Query<(int Id, string Note)>(
            sql, r => (r.GetInt32(0), ReadString(r, 1)), ("$date", date));
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
                  WHERE work_items.create_date = $date
                  ORDER BY work_tags.tag_level;
                  """;
        var rows = Query<(WorkTag Tag, int WorkId)>(
            sql, r => (MapWorkTag(r), r.GetInt32(5)), ("$date", date));
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

    public override Dictionary<int, WorkTimeEntry> GetWorkTimeEntriesByDate(string date)
    {
        var sql = """
                  SELECT redmine_time_entries.*
                  FROM redmine_time_entries INNER JOIN work_items ON redmine_time_entries.work_id = work_items.id
                  WHERE work_items.create_date = $date;
                  """;
        var entries = Query<WorkTimeEntry>(sql, MapWorkTimeEntry, ("$date", date));
        var result = new Dictionary<int, WorkTimeEntry>();
        foreach (var entry in entries)
            result[entry.WorkId] = entry;
        return result;
    }

    public override bool WorkItemAddTag(WorkItem item, WorkTag tag)
    {
        const string sql =
            @"INSERT INTO work_item_tags VALUES($work_id, $tag_id) RETURNING *;";
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$work_id", item.Id);
            cmd.Parameters.AddWithValue("$tag_id", tag.Id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (SQLiteException)
        {
            return false;
        }
    }

    public override bool WorkItemRemoveTag(WorkItem item, WorkTag tag)
    {
        const string sql =
            @"DELETE from work_item_tags WHERE work_id=$work_id and tag_id=$tag_id;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$work_id", item.Id);
        cmd.Parameters.AddWithValue("$tag_id", tag.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override bool WorkItemCleanTags(WorkItem item)
    {
        const string sql =
            @"DELETE from work_item_tags WHERE work_id=$work_id;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$work_id", item.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override ICollection<WorkTag> GetWorkItemTags(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentException("work id is required");
        var sql = """
                  SELECT work_tags.* 
                  FROM work_item_tags INNER JOIN work_tags ON work_item_tags.tag_id=work_tags.id
                  WHERE work_item_tags.work_id = $work_id
                  ORDER BY work_tags.tag_level ASC;
                  """;
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$work_id", item.Id);
        using var reader = cmd.ExecuteReader();
        var tags = new List<WorkTag>();
        while (reader.Read())
        {
            tags.Add(MapWorkTag(reader));
        }

        return tags;
    }

    public override RedMineActivity AddRedMineActivity(int id, string title)
    {
        const string sql =
            @"INSERT INTO redmine_activities VALUES ($id,$title) ON CONFLICT(id) DO UPDATE SET act_name=$title RETURNING *;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return MapRedMineActivity(reader);
        }

        return new RedMineActivity();
    }

    public override RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project,
        bool closed = false)
    {
        const string sql =
            "INSERT INTO redmine_issues(id, issue_title, assigned_to, project_id, is_closed) VALUES ($id,$title,$assign,$project,$close) ON CONFLICT(id) DO UPDATE SET issue_title=$title, assigned_to=$assign, project_id=$project, is_closed=$close RETURNING *;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$assign", assignedTo);
        cmd.Parameters.AddWithValue("$project", project);
        cmd.Parameters.AddWithValue("$close", closed ? 1 : 0);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return MapRedMineIssue(reader);
        }

        return new RedMineIssue();
    }

    public override void UpdateRedMineIssueStatus(int id, bool closed)
    {
        const string sql =
            @"UPDATE redmine_issues SET is_closed=$closed WHERE id=$id;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$closed", closed ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public override RedMineProject AddRedMineProject(int id, string title, string description)
    {
        const string sql =
            @"INSERT INTO redmine_projects(id, project_name, project_desc) VALUES ($id,$title,$desc) ON CONFLICT(id) DO UPDATE SET project_name=$title, project_desc=$desc RETURNING *;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$desc", description);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return MapRedMineProject(reader);
        }

        return new RedMineProject();
    }

    public override void UpdateRedMineProjectStatus(int id, bool closed)
    {
        const string sql =
            @"UPDATE redmine_projects SET is_closed=$closed WHERE id=$id;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$closed", closed ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public override WorkTimeEntry? WorkItemGetTimeEntry(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentException("work id is required");
        var sql = """
                  SELECT * FROM redmine_time_entries WHERE work_id=$id;
                  """;
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", item.Id);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return MapWorkTimeEntry(reader);
        }

        return null;
    }

    public override bool WorkItemWasUploaded(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentException("work id is required");
        var sql = """
                  SELECT * FROM redmine_time_entries WHERE work_id=$id AND id>0;
                  """;
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", item.Id);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    public override ICollection<RedMineActivity> GetRedMineActivities()
    {
        var sql = @"SELECT * FROM redmine_activities;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var activities = new List<RedMineActivity>();
        while (reader.Read())
        {
            activities.Add(MapRedMineActivity(reader));
        }

        return activities;
    }

    public override ICollection<RedMineIssueDisplay> GetRedMineIssues(RedMineProject? project)
    {
        if (project == null)
        {
            var sql = """
                      SELECT
                          redmine_issues.id AS id, redmine_issues.issue_title, redmine_issues.assigned_to, redmine_projects.project_name, redmine_issues.is_closed as closed
                      FROM
                          redmine_issues INNER JOIN redmine_projects ON redmine_issues.project_id=redmine_projects.id ORDER BY closed ASC, id DESC;
                      """;
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var activities = new List<RedMineIssueDisplay>();
            while (reader.Read())
            {
                activities.Add(new RedMineIssueDisplay()
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    AssignedTo = reader.GetString(2),
                    Project = reader.GetString(3),
                    Disabled = reader.GetInt32(4) != 0,
                });
            }

            return activities;
        }
        else
        {
            var sql = """
                      SELECT
                          redmine_issues.id AS id, redmine_issues.issue_title, redmine_issues.assigned_to, redmine_projects.project_name, redmine_issues.is_closed as closed
                      FROM
                          redmine_issues INNER JOIN redmine_projects ON redmine_issues.project_id=$projectId AND redmine_issues.project_id=redmine_projects.id ORDER BY closed ASC, id DESC;
                      """;
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$projectId", project.Id);
            using var reader = cmd.ExecuteReader();
            var activities = new List<RedMineIssueDisplay>();
            while (reader.Read())
            {
                activities.Add(new RedMineIssueDisplay()
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    AssignedTo = reader.GetString(2),
                    Project = reader.GetString(3),
                    Disabled = reader.GetInt32(4) != 0,
                });
            }

            return activities;
        }
    }

    public override ICollection<RedMineProject> GetRedMineProjects()
    {
        var sql = @"SELECT * FROM redmine_projects;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var projects = new List<RedMineProject>();
        while (reader.Read())
        {
            projects.Add(MapRedMineProject(reader));
        }

        return projects;
    }

    public override WorkTimeEntry? CreateWorkTimeEntry(int work, int activity, int issue)
    {
        if (work == 0)
        {
            throw new ArgumentException($"Work ID {work} is invalid");
        }

        const string sql =
            "INSERT INTO redmine_time_entries(work_id, act_id, issue_id) VALUES ($workId, $actId, $issueId) ON CONFLICT DO UPDATE SET act_id=$actId, issue_id=$issueId RETURNING *;";
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$workId", work);
            cmd.Parameters.AddWithValue("$actId", activity);
            cmd.Parameters.AddWithValue("$issueId", issue);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return MapWorkTimeEntry(reader);
            }
        }
        catch (SQLiteException)
        {
            return null;
        }
        return null;
    }

    public override bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry)
    {
        if (timeEntry.WorkId == 0)
        {
            throw new ArgumentException($"Work ID {timeEntry.WorkId} is invalid");
        }

        const string sql =
            "UPDATE redmine_time_entries SET act_id=$actId, issue_id=$issueId, id=$entryId WHERE work_id=$workId;";
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$actId", timeEntry.ActivityId);
        cmd.Parameters.AddWithValue("$issueId", timeEntry.IssueId);
        cmd.Parameters.AddWithValue("$entryId", timeEntry.EntryId);
        cmd.Parameters.AddWithValue("$workId", timeEntry.WorkId);
        return cmd.ExecuteNonQuery() > 0;
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
            var dateRangeQuery = "SELECT sum(hours) FROM work_items WHERE create_date BETWEEN $beginDate AND $endDate;";
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = dateRangeQuery;
            cmd.Parameters.AddWithValue("$beginDate", beginDate);
            cmd.Parameters.AddWithValue("$endDate", endDate);
            using var reader = cmd.ExecuteReader();
            if (reader.Read() && !reader.IsDBNull(0))
                result.Total = reader.GetDouble(0);
        }

        if (result.Total > 0)
        {
            var sql = """
                      SELECT work_tags.id AS tid, sum(hours) AS total, tag_name 
                      FROM 
                      	((work_item_tags INNER JOIN
                      			(SELECT id,hours FROM work_items WHERE create_date BETWEEN $beginDate AND $endDate) AS T1
                      		ON work_item_tags.work_id=T1.id) AS T2
                      	INNER JOIN work_tags ON work_tags.id=T2.tag_id AND work_tags.tag_level=0)
                      GROUP BY tid;
                      """;

            // 一级标签
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$beginDate", beginDate);
            cmd.Parameters.AddWithValue("$endDate", endDate);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.PrimaryTags.Add(new TagTime()
                {
                    TagId = reader.GetInt32(0),
                    Time = reader.GetDouble(1),
                    TagName = reader.GetString(2),
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
                            	INNER JOIN (SELECT id, hours FROM work_items WHERE create_date BETWEEN $beginDate AND $endDate) AS T1
                            		ON primary_tags.work_id = T1.id
                            GROUP BY primary_tags.tag_id, work_tags.id, work_tags.tag_name;
                            """;

            using var nestedCmd = _connection!.CreateCommand();
            nestedCmd.CommandText = nestedSql;
            nestedCmd.Parameters.AddWithValue("$beginDate", beginDate);
            nestedCmd.Parameters.AddWithValue("$endDate", endDate);
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
                        Time = nestedReader.GetDouble(2),
                        TagName = nestedReader.GetString(3),
                    });
                }
            }
        }

        return result;
    }

    public override ICollection<WorkItem> GetWorkItemsByTagAndDate(string dateBegin, string dateEnd, int l1, int l2 = 0)
    {
        var result = new List<WorkItem>();
        if (l2 == 0)
        {
            var sql = """
                      SELECT work_items.* FROM
                      (work_items INNER JOIN work_item_tags on work_items.id = work_item_tags.work_id)
                      WHERE work_item_tags.tag_id = $id AND work_items.create_date BETWEEN $begin AND $end
                      ORDER BY create_date,work_items.id;
                      """;
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$begin", dateBegin);
            cmd.Parameters.AddWithValue("$end", dateEnd);
            cmd.Parameters.AddWithValue("$id", l1);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(MapWorkItem(reader));
            }
        }
        else
        {
            var sql = """
                      SELECT work_items.* FROM
                      work_items INNER JOIN
                      (SELECT work_item_tags.work_id FROM
                      	(SELECT work_id FROM work_item_tags WHERE tag_id=$primary) AS T0
                      	INNER JOIN work_item_tags ON T0.work_id = work_item_tags.work_id AND work_item_tags.tag_id=$secondary) AS T1
                      	ON work_items.id=T1.work_id WHERE create_date BETWEEN $begin AND $end
                      ORDER BY create_date,id;
                      """;
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$begin", dateBegin);
            cmd.Parameters.AddWithValue("$end", dateEnd);
            cmd.Parameters.AddWithValue("$primary", l1);
            cmd.Parameters.AddWithValue("$secondary", l2);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(MapWorkItem(reader));
            }
        }

        return result;
    }

    public override bool DropData()
    {
        using var transaction = _connection!.BeginTransaction();
        try
        {
            var sql = """
                      DELETE FROM work_item_tags;
                      DELETE FROM work_tags;
                      DELETE FROM work_notes;
                      DELETE FROM redmine_time_entries;
                      DELETE FROM redmine_activities;
                      DELETE FROM redmine_issues;
                      DELETE FROM redmine_projects;
                      DELETE FROM work_items;
                      """;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (Exception)
        {
            transaction.Rollback();
            return false;
        }

        return true;
    }

    public override bool BeginTransaction()
    {
        Debug.Assert(_transaction == null);

        try
        {
            _transaction = _connection!.BeginTransaction();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override bool CommitTransaction()
    {
        Debug.Assert(_transaction != null);

        try
        {
            _transaction.Commit();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    public override bool RollbackTransaction()
    {
        Debug.Assert(_transaction != null);

        try
        {
            _transaction.Rollback();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
        }
    }

    public void Dispose()
    {
        _transaction?.Dispose();
        _connection?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction != null) await _transaction.DisposeAsync();
        if (_connection != null) await _connection.DisposeAsync();
    }
}