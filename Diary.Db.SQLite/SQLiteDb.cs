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
    public override object? Config =>  _config;
    public override bool Connect()
    {
        var csb = new SQLiteConnectionStringBuilder
        {
            DataSource = _config.FilePath,
        };
        _connection = new SQLiteConnection(csb.ToString());
        _connection.Open();
        
        // query version
        var cmd =  _connection.CreateCommand();
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
                            	WorkTags(
                            		Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            		Name CHAR(64) NOT NULL UNIQUE,
                            		Color INTEGER NOT NULL DEFAULT 0,
                            		Level INTEGER NOT NULL DEFAULT 0,
                            		Disabled INTEGER NOT NULL DEFAULT 0
                            	);
                            	
                            CREATE TABLE IF NOT EXISTS
                            	WorkItems(
                            		Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            		CreateDate CHAR(16) NOT NULL,
                            		Comment CHAR(128) NOT NULL,
                            		Hours REAL DEFAULT 0.0,
                            		Priority INTEGER DEFAULT 0
                            	);
                            
                            CREATE TABLE IF NOT EXISTS
                            	WorkNotes(
                            		WorkId INTEGER PRIMARY KEY
                            			REFERENCES WorkItems(Id)
                            			ON DELETE CASCADE,
                            		Note TEXT NOT NULL
                            	);
                            
                            	
                            CREATE TABLE IF NOT EXISTS
                            	WorkItemTags(
                            		WorkId INTEGER REFERENCES WorkItems(Id),
                            		TagId INTEGER REFERENCES WorkTags(Id),
                            		PRIMARY KEY (WorkId,TagId)
                            	);
                            	
                            CREATE TABLE IF NOT EXISTS
                            	RedMineProjects(
                            		Id INTEGER NOT NULL PRIMARY KEY,
                            		Title CHAR(128) NOT NULL,
                            		Description CHAR(1024) DEFAULT '',
                            		IsClosed INTEGER DEFAULT 0
                            	);
                            	
                            CREATE TABLE IF NOT EXISTS
                            	RedMineActivities(
                            		Id INTEGER PRIMARY KEY,
                            		Title CHAR(32) NOT NULL
                            	);
                            	
                            CREATE TABLE IF NOT EXISTS
                            	RedMineIssues(
                            		Id INTEGER PRIMARY KEY,
                            		Title CHAR(128) NOT NULL,
                            		AssignedTo CHAR(16) DEFAULT '',
                            		ProjectId INTEGER NOT NULL REFERENCES
                            			RedMineProjects(Id) ON DELETE CASCADE,
                            		IsClosed INTEGER default 0
                            	);
                            	
                            CREATE TABLE IF NOT EXISTS
                            	RedMineTimeEntries(
                            		WorkId INTEGER PRIMARY KEY
                            			REFERENCES WorkItems(Id) ON DELETE CASCADE,
                            		EntryId INTEGER DEFAULT 0,
                            		ActivityId INTEGER
                            			REFERENCES RedMineActivities(Id) ON DELETE SET NULL,
                            		IssueId INTEGER
                            			REFERENCES RedMineIssues(Id) ON DELETE SET NULL
                            	);
                            
                            CREATE TABLE IF NOT EXISTS
                            	DataVersions(
                            		Code INTEGER PRIMARY KEY
                            	);
                            
                            -- default data version is 1.0.0 (0x1000000)
                            INSERT INTO DataVersions VALUES(0x10000) ON CONFLICT DO NOTHING;
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
        cmd.CommandText = "SELECT * FROM DataVersions ORDER BY Code DESC LIMIT 1;";
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
        throw new NotImplementedException();
    }

    public override bool UpdateWorkTag(WorkTag tag)
    {
        throw new NotImplementedException();
    }

    public override bool DeleteWorkTag(WorkTag tag)
    {
        throw new NotImplementedException();
    }

    public override ICollection<WorkTag> AllWorkTags()
    {
        throw new NotImplementedException();
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
