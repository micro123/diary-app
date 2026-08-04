using System.Data.Common;
using System.Data.SQLite;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.PluginBase;
using Diary.RedMine;
using Diary.RedMine.Models;

namespace Diary.Db.SQLite;

/// <summary>
/// SQLite 的 RedMine 数据访问实现。从 <see cref="SQLiteDb"/> 迁出，
/// 经由中性 <see cref="IDbExtensionHost"/> 跑 SQL（<c>$name</c> 命名占位符）。
/// SQL 与 mapper 与迁出前逐字符一致，行为零变化。
/// </summary>
public sealed class SQLiteRedMineDb(IDbExtensionHost host, string instanceId) : IRedMineDb
{
    private const uint CurrentSchemaVersion = 2;
    private readonly IDbExtensionHost _host = host;

    public string InstanceId => instanceId;

    public uint SchemaVersion => CurrentSchemaVersion;

    public bool Initialize()
    {
        try
        {
            const string versionTable = """
                                        CREATE TABLE IF NOT EXISTS plugin_data_versions(
                                            plugin_id CHAR(128) PRIMARY KEY,
                                            schema_version INTEGER NOT NULL
                                        );
                                        """;
            if (!_host.ExecRaw(versionTable))
                return false;

            var schemaVersion = GetSchemaVersion();
            if (HasLegacyTables())
            {
                schemaVersion = HasInstanceColumn("redmine_issues") ? schemaVersion : 1;
            }
            else
            {
                schemaVersion = 0;
            }

            var context = new DelegatePluginMigrationContext(
                "SQLite", 0, _host.ExecRaw,
                (sql, map, args) => _host.Query(sql, reader => map(reader),
                    args.Cast<(string Name, object? Value)>().ToArray()));
            if (!PluginMigrationRunner.Upgrade(
                    "tracker.redmine", schemaVersion, CurrentSchemaVersion,
                    new IPluginMigration[] { new RedMineInitialMigration(), new RedMineInstanceMigration() }, context))
            {
                return false;
            }

            return _host.ExecRaw(
                "INSERT INTO plugin_data_versions(plugin_id, schema_version) VALUES ('tracker.redmine', 2) ON CONFLICT(plugin_id) DO UPDATE SET schema_version=2;");
        }
        catch (SQLiteException)
        {
            return false;
        }
    }

    public RedMineActivity AddRedMineActivity(int id, string title)
    {
        const string sql =
            @"INSERT INTO redmine_activities(instance_id,id,act_name) VALUES ($instanceId,$id,$title) ON CONFLICT(instance_id,id) DO UPDATE SET act_name=$title RETURNING id,act_name;";
        return _host.QueryFirst(sql, MapRedMineActivity,
            ("$instanceId", InstanceId), ("$id", id), ("$title", title)) ?? new RedMineActivity();
    }

    public bool ClearData()
    {
        try
        {
            return _host.Execute("DELETE FROM redmine_time_entries WHERE instance_id=$instanceId;", ("$instanceId", InstanceId)) >= 0
                && _host.Execute("DELETE FROM redmine_activities WHERE instance_id=$instanceId;", ("$instanceId", InstanceId)) >= 0
                && _host.Execute("DELETE FROM redmine_issues WHERE instance_id=$instanceId;", ("$instanceId", InstanceId)) >= 0
                && _host.Execute("DELETE FROM redmine_projects WHERE instance_id=$instanceId;", ("$instanceId", InstanceId)) >= 0;
        }
        catch (SQLiteException)
        {
            return false;
        }
    }

    public uint GetSchemaVersion()
    {
        var value = _host.ExecuteScalar(
            "SELECT schema_version FROM plugin_data_versions WHERE plugin_id=$pluginId;",
            ("$pluginId", "tracker.redmine"));
        return value is null ? 0 : Convert.ToUInt32(value);
    }

    private bool HasLegacyTables()
        => _host.ExecuteScalar("SELECT 1 FROM sqlite_master WHERE type='table' AND name='redmine_issues';") is not null;

    private bool HasInstanceColumn(string table)
        => _host.Query($"PRAGMA table_info({table});", reader => _host.ReadString(reader, 1))
            .Contains("instance_id", StringComparer.OrdinalIgnoreCase);

    public RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project,
        bool closed = false)
    {
        const string sql =
            "INSERT INTO redmine_issues(instance_id,id, issue_title, assigned_to, project_id, is_closed) VALUES ($instanceId,$id,$title,$assign,$project,$close) ON CONFLICT(instance_id,id) DO UPDATE SET issue_title=$title, assigned_to=$assign, project_id=$project, is_closed=$close RETURNING id,issue_title,assigned_to,project_id,is_closed;";
        return _host.QueryFirst(sql, MapRedMineIssue,
            ("$instanceId", InstanceId), ("$id", id), ("$title", title), ("$assign", assignedTo),
            ("$project", project), ("$close", closed ? 1 : 0)) ?? new RedMineIssue();
    }

    public void UpdateRedMineIssueStatus(int id, bool closed)
    {
        const string sql = @"UPDATE redmine_issues SET is_closed=$closed WHERE instance_id=$instanceId AND id=$id;";
        _host.Execute(sql, ("$instanceId", InstanceId), ("$id", id), ("$closed", closed ? 1 : 0));
    }

    public RedMineProject AddRedMineProject(int id, string title, string description)
    {
        const string sql =
            @"INSERT INTO redmine_projects(instance_id,id, project_name, project_desc) VALUES ($instanceId,$id,$title,$desc) ON CONFLICT(instance_id,id) DO UPDATE SET project_name=$title, project_desc=$desc RETURNING id,project_name,project_desc,is_closed;";
        return _host.QueryFirst(sql, MapRedMineProject,
            ("$instanceId", InstanceId), ("$id", id), ("$title", title), ("$desc", description)) ?? new RedMineProject();
    }

    public void UpdateRedMineProjectStatus(int id, bool closed)
    {
        const string sql = @"UPDATE redmine_projects SET is_closed=$closed WHERE instance_id=$instanceId AND id=$id;";
        _host.Execute(sql, ("$instanceId", InstanceId), ("$id", id), ("$closed", closed ? 1 : 0));
    }

    public WorkTimeEntry? WorkItemGetTimeEntry(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentException("work id is required");
        var sql = """
                  SELECT work_id,id,act_id,issue_id FROM redmine_time_entries WHERE instance_id=$instanceId AND work_id=$id;
                  """;
        return _host.QueryFirst(sql, MapWorkTimeEntry, ("$instanceId", InstanceId), ("$id", item.Id));
    }

    public IDictionary<int, WorkTimeEntry> GetWorkTimeEntriesByDate(string date)
    {
        const string sql = """
                           SELECT redmine_time_entries.work_id,redmine_time_entries.id,redmine_time_entries.act_id,redmine_time_entries.issue_id
                           FROM redmine_time_entries INNER JOIN work_items ON redmine_time_entries.work_id = work_items.id
                           WHERE redmine_time_entries.instance_id=$instanceId AND work_items.create_date = $date;
                           """;
        var result = new Dictionary<int, WorkTimeEntry>();
        foreach (var entry in _host.Query(sql, MapWorkTimeEntry, ("$instanceId", InstanceId), ("$date", date)))
            result[entry.WorkId] = entry;
        return result;
    }

    public bool WorkItemWasUploaded(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentException("work id is required");
        var sql = """
                  SELECT work_id,id,act_id,issue_id FROM redmine_time_entries WHERE instance_id=$instanceId AND work_id=$id AND id>0;
                  """;
        return _host.Exists(sql, ("$instanceId", InstanceId), ("$id", item.Id));
    }

    public ICollection<RedMineActivity> GetRedMineActivities()
    {
        const string sql = @"SELECT id,act_name FROM redmine_activities WHERE instance_id=$instanceId;";
        return _host.Query(sql, MapRedMineActivity, ("$instanceId", InstanceId));
    }

    public ICollection<RedMineIssueDisplay> GetRedMineIssues(RedMineProject? project)
    {
        RedMineIssueDisplay MapDisplay(DbDataReader r) => new()
        {
            Id = r.GetInt32(0),
            Title = _host.ReadString(r, 1),
            AssignedTo = _host.ReadString(r, 2),
            Project = _host.ReadString(r, 3),
            Disabled = r.GetInt32(4) != 0,
        };

        if (project is null)
        {
            var sql = """
                      SELECT redmine_issues.id AS id, redmine_issues.issue_title, redmine_issues.assigned_to, redmine_projects.project_name, redmine_issues.is_closed AS closed
                      FROM redmine_issues INNER JOIN redmine_projects ON redmine_issues.instance_id=redmine_projects.instance_id AND redmine_issues.project_id=redmine_projects.id
                      WHERE redmine_issues.instance_id=$instanceId
                      ORDER BY closed ASC, id DESC;
                      """;
            return _host.Query(sql, MapDisplay, ("$instanceId", InstanceId));
        }

        var sqlFiltered = """
                          SELECT redmine_issues.id AS id, redmine_issues.issue_title, redmine_issues.assigned_to, redmine_projects.project_name, redmine_issues.is_closed AS closed
                           FROM redmine_issues INNER JOIN redmine_projects ON redmine_issues.instance_id=redmine_projects.instance_id AND redmine_issues.project_id=$projectId AND redmine_issues.project_id=redmine_projects.id
                           WHERE redmine_issues.instance_id=$instanceId
                          ORDER BY closed ASC, id DESC;
                          """;
        return _host.Query(sqlFiltered, MapDisplay, ("$instanceId", InstanceId), ("$projectId", project.Id));
    }

    public ICollection<RedMineProject> GetRedMineProjects()
    {
        const string sql = @"SELECT id,project_name,project_desc,is_closed FROM redmine_projects WHERE instance_id=$instanceId;";
        return _host.Query(sql, MapRedMineProject, ("$instanceId", InstanceId));
    }

    public WorkTimeEntry? CreateWorkTimeEntry(int work, int activity, int issue)
    {
        if (work == 0)
            throw new ArgumentException($"Work ID {work} is invalid");
        const string sql =
            "INSERT INTO redmine_time_entries(instance_id,work_id, act_id, issue_id) VALUES ($instanceId,$workId, $actId, $issueId) ON CONFLICT(instance_id,work_id) DO UPDATE SET act_id=$actId, issue_id=$issueId RETURNING work_id,id,act_id,issue_id;";
        try
        {
            return _host.QueryFirst(sql, MapWorkTimeEntry, ("$instanceId", InstanceId), ("$workId", work), ("$actId", activity), ("$issueId", issue));
        }
        catch (SQLiteException)
        {
            return null;
        }
    }

    public bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry)
    {
        if (timeEntry.WorkId == 0)
            throw new ArgumentException($"Work ID {timeEntry.WorkId} is invalid");
        const string sql =
            "UPDATE redmine_time_entries SET act_id=$actId, issue_id=$issueId, id=$entryId WHERE instance_id=$instanceId AND work_id=$workId;";
        return _host.Execute(sql,
            ("$actId", timeEntry.ActivityId), ("$issueId", timeEntry.IssueId),
            ("$entryId", timeEntry.EntryId), ("$instanceId", InstanceId), ("$workId", timeEntry.WorkId)) > 0;
    }

    // ---- mappers（与迁出前一致；用 _host.ReadString 封装 CHAR padding）----

    private RedMineActivity MapRedMineActivity(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Title = _host.ReadString(r, 1),
    };

    private RedMineProject MapRedMineProject(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Title = _host.ReadString(r, 1),
        Description = _host.ReadString(r, 2),
        IsClosed = r.GetInt32(3) != 0,
    };

    private RedMineIssue MapRedMineIssue(DbDataReader r) => new()
    {
        Id = r.GetInt32(0),
        Title = _host.ReadString(r, 1),
        AssignedTo = _host.ReadString(r, 2),
        ProjectId = r.GetInt32(3),
        IsClosed = r.GetInt32(4) != 0,
    };

    private WorkTimeEntry MapWorkTimeEntry(DbDataReader r) => new()
    {
        WorkId = r.GetInt32(0),
        EntryId = r.GetInt32(1),
        ActivityId = r.GetInt32(2),
        IssueId = r.GetInt32(3),
    };
}
