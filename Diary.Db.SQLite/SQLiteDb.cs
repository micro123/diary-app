using System.Data.SQLite;
using Diary.Core.Data.Base;
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
        var tableInitCmds = """
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
                            			REFERENCES redmine_activities(id) ON DELETE SET NULL,
                            		issue_id INTEGER
                            			REFERENCES redmine_issues(id) ON DELETE SET NULL
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
            cmd.CommandText = tableInitCmds;
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

    public override WorkTag CreateWorkTag(string name)
    {
        const string sql = @"INSERT INTO work_tags(tag_name) VALUES ($value) RETURNING id;";
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$value", name);
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

        return new WorkTag { Name = name };
    }

    public override bool UpdateWorkTag(WorkTag tag)
    {
        if (tag.Id == 0)
        {
            return false;
        }

        const string sql =
            @"UPDATE OR FAIL work_tags SET tag_name=$name, tag_color=$color, tag_level=$level, disabled=$disabled WHERE id=$id;";
        var cmd = _connection!.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$name", tag.Name);
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

    public override WorkItem CreateWorkItem(string date, string comment, string note, double time)
    {
        throw new NotImplementedException();
    }

    public override bool UpdateWorkItem(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public override bool DeleteWorkItem(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public override ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData)
    {
        throw new NotImplementedException();
    }

    public override bool WorkItemAddTag(WorkItem item, WorkTag tag)
    {
        throw new NotImplementedException();
    }

    public override bool WorkItemRemoveTag(WorkItem item, WorkTag tag)
    {
        throw new NotImplementedException();
    }

    public override bool WorkItemCleanTags(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public override ICollection<WorkItemTag> GetWorkItemTags(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public override RedMineActivity AddRedMineActivity(int id, string title)
    {
        throw new NotImplementedException();
    }

    public override RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project)
    {
        throw new NotImplementedException();
    }

    public override RedMineProject AddRedMineProject(int id, string title)
    {
        throw new NotImplementedException();
    }

    public override ICollection<RedMineActivity> GetRedMineActivities()
    {
        throw new NotImplementedException();
    }

    public override ICollection<RedMineIssue> GetRedMineIssues()
    {
        throw new NotImplementedException();
    }

    public override ICollection<RedMineIssue> GetRedMineIssues(RedMineProject project)
    {
        throw new NotImplementedException();
    }

    public override ICollection<RedMineProject> GetRedMineProjects()
    {
        throw new NotImplementedException();
    }

    public override WorkTimeEntry CreateWorkTimeEntry(WorkItem work, RedMineActivity activity, RedMineIssue issue)
    {
        throw new NotImplementedException();
    }

    public override bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry)
    {
        throw new NotImplementedException();
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