using System.Data.SQLite;
using System.Diagnostics;
using Diary.Core.Data.Base;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;
using Diary.Core.Data.Statistics;
using Diary.Database;
using Diary.Utils;

namespace Diary.Db.SQLite;

public sealed class SQLiteDb(IDbFactory factory) : DbInterfaceBase, IDisposable, IAsyncDisposable
{
    private readonly IDbFactory _factory = factory;

    private SQLiteConnection? _connection;
    private SQLiteTransaction? _transaction;

    #region helpers

    private static WorkTag MapWorkTag(SQLiteDataReader reader)
    {
        return new WorkTag()
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            Color = reader.GetInt32(2),
            Level = (TagLevels)reader.GetInt32(3),
            Disabled = reader.GetInt32(4) != 0,
        };
    }

    private static WorkItem MapWorkItem(SQLiteDataReader reader)
    {
        return new WorkItem()
        {
            Id = reader.GetInt32(0),
            CreateDate = reader.GetString(1),
            Comment = reader.GetString(2),
            Time = reader.GetFloat(3),
            Priority = (WorkPriorities)reader.GetInt32(4),
        };
    }

    private RedMineActivity MapRedMineActivity(SQLiteDataReader reader)
    {
        return new RedMineActivity()
        {
            Id = reader.GetInt32(0),
            Title = reader.GetString(1),
        };
    }

    private RedMineProject MapRedMineProject(SQLiteDataReader reader)
    {
        return new RedMineProject()
        {
            Id = reader.GetInt32(0),
            Title = reader.GetString(1),
            Description = reader.GetString(2),
            IsClosed = reader.GetInt32(3) != 0,
        };
    }

    private RedMineIssue MapRedMineIssue(SQLiteDataReader reader)
    {
        return new RedMineIssue()
        {
            Id = reader.GetInt32(0),
            Title = reader.GetString(1),
            AssignedTo = reader.GetString(2),
            ProjectId = reader.GetInt32(3),
            IsClosed = reader.GetInt32(4) != 0,
        };
    }

    private WorkTimeEntry MapWorkTimeEntry(SQLiteDataReader reader)
    {
        return new WorkTimeEntry()
        {
            WorkId = reader.GetInt32(0),
            EntryId = reader.GetInt32(1),
            ActivityId = reader.GetInt32(2),
            IssueId = reader.GetInt32(3),
        };
    }

    #endregion

    public override bool Connect()
    {
        var cfg = _factory.GetConfig() as Config;
        Debug.Assert(cfg != null);
        var csb = new SQLiteConnectionStringBuilder
        {
            DataSource = cfg.FilePath,
        };
        _connection = new SQLiteConnection(csb.ToString());
        _connection.Open();

        // query version
        var cmd = _connection.CreateCommand();
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
                                    """;
        using var transaction = _connection!.BeginTransaction();
        try
        {
            var cmd = _connection.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = "SELECT * FROM data_versions ORDER BY version_code DESC LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
            return (uint)reader.GetInt32(0);
        return 0;
    }

    public override bool UpdateTables(uint targetVersion)
    {
        var currentVersion = GetDataVersion();
        while (currentVersion != targetVersion)
        {
            var migration = _factory.GetMigration(currentVersion);
            if (migration == null)
                return false;
            migration.Up(this);
            currentVersion = GetDataVersion();
        }

        return true;
    }

    public override WorkTag CreateWorkTag(string name, bool primary, int color)
    {
        const string sql =
            @"INSERT OR IGNORE INTO work_tags(tag_name,tag_level,tag_color) VALUES ($value,$level,$color) RETURNING *;";
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", tag.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override ICollection<WorkTag> AllWorkTags()
    {
        List<WorkTag> result = new();

        const string sql = @"SELECT * FROM work_tags ORDER BY is_disabled ASC, tag_level ASC;";
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$old", oldId);
        cmd.Parameters.AddWithValue("$new", newId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override WorkItem CreateWorkItem(string date, string comment)
    {
        const string sql =
            @"INSERT INTO work_items(create_date, comment) VALUES ($create_date, $comment) RETURNING *;";
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", work.Id);
        cmd.Parameters.AddWithValue("$note", content);
        cmd.ExecuteNonQuery();
    }

    public override void WorkDeleteNote(WorkItem work)
    {
        const string sql =
            @"DELETE FROM work_notes WHERE id=$id;";
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", work.Id);
        cmd.ExecuteNonQuery();
    }

    public override string? WorkGetNote(WorkItem work)
    {
        const string sql =
            @"SELECT note FROM work_notes WHERE id=$id;";
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", work.Id);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return reader.GetString(0);
        }

        return null;
    }

    public override bool WorkItemAddTag(WorkItem item, WorkTag tag)
    {
        const string sql =
            @"INSERT INTO work_item_tags VALUES($work_id, $tag_id) RETURNING *;";
        try
        {
            var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$work_id", item.Id);
        cmd.Parameters.AddWithValue("$tag_id", tag.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override bool WorkItemCleanTags(WorkItem item)
    {
        const string sql =
            @"DELETE from work_item_tags WHERE work_id=$work_id;";
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$closed", closed ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public override RedMineProject AddRedMineProject(int id, string title, string description)
    {
        const string sql =
            @"INSERT INTO redmine_projects(id, project_name, project_desc) VALUES ($id,$title,$desc) ON CONFLICT(id) DO UPDATE SET project_name=$title, project_desc=$desc RETURNING *;";
        var cmd = _connection!.CreateCommand();
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
            @"UPDATE redmine_projects SET is_closed=@closed WHERE id=$id;";
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", item.Id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public override ICollection<RedMineActivity> GetRedMineActivities()
    {
        var sql = @"SELECT * FROM redmine_activities;";
        var cmd = _connection!.CreateCommand();
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
            var cmd = _connection!.CreateCommand();
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
            var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
            var cmd = _connection!.CreateCommand();
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
        var cmd = _connection!.CreateCommand();
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
            var cmd = _connection!.CreateCommand();
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
            var cmd = _connection!.CreateCommand();
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

        foreach (var tag in result.PrimaryTags)
        {
            var sql = """
                      SELECT
                      	work_tags.id, sum(hours) as total, work_tags.tag_name
                      FROM
                      	((((SELECT work_id FROM work_item_tags WHERE tag_id=$tagId) AS T0 INNER JOIN
                      	work_item_tags ON t0.work_id=work_item_tags.work_id AND work_item_tags.tag_id!=$tagId) INNER JOIN
                      	(SELECT id, hours FROM work_items WHERE create_date BETWEEN $beginDate AND $endDate) AS T1 ON T0.work_id=T1.id) AS T2 INNER JOIN
                      	work_tags ON work_tags.id=T2.tag_id AND work_tags.tag_level!=0)
                      GROUP BY work_tags.id;
                      """;

            var cmd = _connection!.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$beginDate", beginDate);
            cmd.Parameters.AddWithValue("$endDate", endDate);
            cmd.Parameters.AddWithValue("$tagId", tag.TagId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tag.Nested.Add(new TagTime()
                {
                    TagId = reader.GetInt32(0),
                    Time = reader.GetDouble(1),
                    TagName = reader.GetString(2),
                });
            }
        }

        return result;
    }

    public override StatisticsResult GetStatistics()
    {
        // get date range
        var sql = "SELECT min(create_date), max(create_date) FROM work_items;";
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        if (reader.Read() && !reader.IsDBNull(0))
        {
            var beginDate = reader.GetString(0);
            var endDate = reader.GetString(1);
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
            var cmd = _connection!.CreateCommand();
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
            var cmd = _connection!.CreateCommand();
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
            var cmd = _connection.CreateCommand();
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