using System.Data.SQLite;
using Diary.Core.Data.Base;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;
using Diary.Database;
using Diary.Utils;

namespace Diary.Db.SQLite;

public sealed class SQLiteDb(IDbFactory factory) : DbInterfaceBase, IDisposable, IAsyncDisposable
{
    private readonly IDbFactory _factory = factory;

    private readonly Config _config = new()
    {
        FilePath = Path.Combine(FsTools.GetApplicationDataDirectory(), "db.sqlite3"),
    };

    private SQLiteConnection? _connection;
    public override object? Config => _config;

    public override bool Connect()
    {
        var csb = new SQLiteConnectionStringBuilder
        {
            DataSource = _config.FilePath,
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
                                    		comment CHAR(128) NOT NULL,
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
                                    		work_id INTEGER REFERENCES work_items(id),
                                    		tag_id INTEGER REFERENCES work_tags(id),
                                    		PRIMARY KEY (work_id,tag_id)
                                    	);
                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	redmine_projects(
                                    		id INTEGER NOT NULL PRIMARY KEY,
                                    		project_name CHAR(128) NOT NULL,
                                    		project_desc CHAR(1024) DEFAULT '',
                                    		is_closed INTEGER DEFAULT 0
                                    	);
                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	redmine_activities(
                                    		id INTEGER PRIMARY KEY,
                                    		act_name CHAR(32) NOT NULL
                                    	);
                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	redmine_issues(
                                    		id INTEGER PRIMARY KEY,
                                    		issue_title CHAR(128) NOT NULL,
                                    		assigned_to CHAR(16) DEFAULT '',
                                    		project_id INTEGER NOT NULL REFERENCES
                                    			redmine_projects(id) ON DELETE CASCADE,
                                    		is_closed INTEGER default 0
                                    	);
                                    	
                                    CREATE TABLE IF NOT EXISTS
                                    	redmine_time_entries(
                                    		work_id INTEGER PRIMARY KEY
                                    			REFERENCES work_items(id) ON DELETE CASCADE,
                                    		id INTEGER DEFAULT 0,
                                    		act_id INTEGER
                                    			REFERENCES redmine_activities(id) ON DELETE CASCADE,
                                    		issue_id INTEGER
                                    			REFERENCES redmine_issues(id) ON DELETE CASCADE
                                    	);

                                    CREATE TABLE IF NOT EXISTS
                                    	data_versions(
                                    		version_code INTEGER PRIMARY KEY
                                    	);

                                    -- default data version is 1.0.0 (0x1000000)
                                    INSERT INTO data_versions VALUES(0x10000) ON CONFLICT DO NOTHING;
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
        cmd.Parameters.AddWithValue("$level", primary ? 1 : 0);
        cmd.Parameters.AddWithValue("$color", color);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
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

        const string sql = @"SELECT * FROM work_tags;";
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(
                new WorkTag
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Color = reader.GetInt32(2),
                    Level = (TagLevels)reader.GetInt32(3),
                    Disabled = reader.GetInt32(4) != 0,
                }
            );
        }

        return result;
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
            return new WorkItem()
            {
                Id = reader.GetInt32(0),
                CreateDate = reader.GetString(1),
                Comment = reader.GetString(2),
                Time = reader.GetDouble(3),
                Priority = (WorkPriorities)reader.GetInt32(4),
            };
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
        throw new NotImplementedException();
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
            items.Add(new WorkItem()
            {
                Id = reader.GetInt32(0),
                CreateDate = reader.GetString(1),
                Comment = reader.GetString(2),
                Time = reader.GetDouble(3),
                Priority = (WorkPriorities)reader.GetInt32(4),
            });
        }

        return items;
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
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$work_id", item.Id);
        cmd.Parameters.AddWithValue("$tag_id", tag.Id);
        return cmd.ExecuteNonQuery() > 0;
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
        throw new NotImplementedException();
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
            return new RedMineActivity()
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
            };
        }

        return new RedMineActivity();
    }

    public override RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project)
    {
        const string sql =
            @"INSERT INTO redmine_issues(id, issue_title, assigned_to, project_id) VALUES ($id,$title,$assign,$project) ON CONFLICT(id) DO UPDATE SET issue_title=$title, assigned_to=$assign, project_id=$project RETURNING *;";
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$assign", assignedTo);
        cmd.Parameters.AddWithValue("$project", project);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
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

        return new RedMineIssue();
    }

    public override void UpdateRedMineIssueStatus(int id, bool closed)
    {
        const string sql =
            @"UPDATE redmine_issues SET is_closed=@closed WHERE id=$id;";
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
            return new RedMineProject()
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                Description = reader.GetString(2),
                IsClosed = reader.GetInt32(3) != 0,
            };
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

    public override ICollection<RedMineActivity> GetRedMineActivities()
    {
        var sql = @"SELECT * FROM redmine_activities;";
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var activities = new List<RedMineActivity>();
        while (reader.Read())
        {
            activities.Add(new RedMineActivity()
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
            });
        }

        return activities;
    }

    public override ICollection<RedMineIssueDisplay> GetRedMineIssues(RedMineProject? project)
    {
        if (project == null)
        {
            var sql = """
                      SELECT
                          redmine_issues.id AS id, redmine_issues.issue_title, redmine_issues.assigned_to, redmine_projects.project_name, redmine_issues.is_closed
                      FROM
                          redmine_issues INNER JOIN redmine_projects WHERE redmine_issues.project_id=redmine_projects.id ORDER BY ID DESC;
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
                          redmine_issues.id AS id, redmine_issues.issue_title, redmine_issues.assigned_to, redmine_projects.project_name, redmine_issues.is_closed
                      FROM
                          redmine_issues INNER JOIN redmine_projects WHERE redmine_issues.project_id=$projectId AND redmine_issues.project_id=redmine_projects.id ORDER BY ID DESC;
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
        var activities = new List<RedMineProject>();
        while (reader.Read())
        {
            activities.Add(new RedMineProject()
            {
                Id = reader.GetInt32(0),
                Title = reader.GetString(1),
                Description = reader.GetString(2),
                IsClosed = reader.GetInt32(3) != 0,
            });
        }

        return activities;
    }

    public override WorkTimeEntry CreateWorkTimeEntry(WorkItem work, RedMineActivity activity, RedMineIssue issue)
    {
        if (work.Id == 0)
        {
            throw new ArgumentException($"Work ID {work.Id} is invalid");
        }

        const string sql =
            "INSERT INTO redmine_time_entries(work_id, act_id, issue_id) VALUES ($workId, $actId, $issueId) ON CONFLICT DO UPDATE SET act_id=$actId, issue_id=$issueId RETURNING *;";
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$workId", work.Id);
        cmd.Parameters.AddWithValue("$actId", activity.Id);
        cmd.Parameters.AddWithValue("$issueId", issue.Id);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new WorkTimeEntry()
            {
                WorkId = reader.GetInt32(0),
                EntryId = reader.GetInt32(1),
                ActivityId = reader.GetInt32(2),
                IssueId = reader.GetInt32(3),
            };
        }

        return new WorkTimeEntry();
    }

    public override bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry)
    {
        if (timeEntry.WorkId == 0)
        {
            throw new ArgumentException($"Work ID {timeEntry.WorkId} is invalid");
        }

        const string sql =
            "UPDATE redmine_time_entries SET act_id=$actId, issue_id=$issueId, id=$entryId;";
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$actId", timeEntry.ActivityId);
        cmd.Parameters.AddWithValue("$issueId", timeEntry.IssueId);
        cmd.Parameters.AddWithValue("$entryId", timeEntry.EntryId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null) await _connection.DisposeAsync();
    }
}