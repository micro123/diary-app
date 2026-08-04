using System.Data.Common;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.PluginBase;
using Diary.RedMine;
using Diary.RedMine.Models;

namespace Diary.Db.PostgreSQL;

/// <summary>
/// PostgreSQL 的 RedMine 数据访问实现。从 <see cref="PgDb"/> 迁出，
/// 经由中性 <see cref="IDbExtensionHost"/> 跑 SQL（<c>$1..$n</c> 位置占位符，
/// 由 <see cref="PgDb.BindParameter"/> 按位置绑定）。
/// SQL 与 mapper 与迁出前逐字符一致，行为零变化。
/// </summary>
public sealed class PgRedMineDb(IDbExtensionHost host) : IRedMineDb
{
    private const uint CurrentSchemaVersion = 1;
    private readonly IDbExtensionHost _host = host;

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

            var context = new DelegatePluginMigrationContext(
                "PostgreSQL", 0, _host.ExecRaw,
                (sql, map, args) => _host.Query(sql, reader => map(reader),
                    args.Cast<(string Name, object? Value)>().ToArray()));
            if (!PluginMigrationRunner.Upgrade(
                    "tracker.redmine", GetSchemaVersion(), CurrentSchemaVersion,
                    new[] { new RedMineInitialMigration() }, context))
            {
                return false;
            }

            return _host.ExecRaw(
                "INSERT INTO plugin_data_versions(plugin_id, schema_version) VALUES ('tracker.redmine', 1) ON CONFLICT(plugin_id) DO UPDATE SET schema_version=1;");
        }
        catch (Exception)
        {
            return false;
        }
    }

    // $1=id $2=title
    public RedMineActivity AddRedMineActivity(int id, string title)
    {
        var sql = """
                  INSERT INTO redmine_activities(id, act_name) VALUES ($1,$2) ON CONFLICT (id) DO UPDATE SET act_name=$2 RETURNING *;
                  """;
        return _host.QueryFirst(sql, MapRedMineActivity, ("$1", id), ("$2", title)) ?? new RedMineActivity();
    }

    public bool ClearData()
    {
        const string sql = """
                           DELETE FROM redmine_time_entries;
                           DELETE FROM redmine_activities;
                           DELETE FROM redmine_issues;
                           DELETE FROM redmine_projects;
                           """;
        try
        {
            return _host.ExecRaw(sql);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public uint GetSchemaVersion()
    {
        var value = _host.ExecuteScalar(
            "SELECT schema_version FROM plugin_data_versions WHERE plugin_id=$1;",
            ("$1", "tracker.redmine"));
        return value is null ? 0 : Convert.ToUInt32(value);
    }

    // $1=id $2=title $3=assign $4=project $5=close
    public RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project,
        bool closed = false)
    {
        var sql = """
                  INSERT INTO redmine_issues(id, issue_title, assigned_to, project_id, is_closed)
                  VALUES ($1,$2,$3,$4,$5) ON CONFLICT(id) DO UPDATE SET
                  issue_title=$2,assigned_to=$3,project_id=$4,is_closed=$5 RETURNING *;
                  """;
        return _host.QueryFirst(sql, MapRedMineIssue,
            ("$1", id), ("$2", title), ("$3", assignedTo),
            ("$4", project), ("$5", closed ? 1 : 0)) ?? new RedMineIssue();
    }

    // $1=id $2=closed
    public void UpdateRedMineIssueStatus(int id, bool closed)
    {
        var sql = """
                  UPDATE redmine_issues SET is_closed=$2 WHERE id=$1;
                  """;
        _host.Execute(sql, ("$1", id), ("$2", closed ? 1 : 0));
    }

    // $1=id $2=title $3=desc
    public RedMineProject AddRedMineProject(int id, string title, string description)
    {
        var sql = """
                  INSERT INTO redmine_projects(id, project_name, project_desc)
                  VALUES ($1,$2,$3) ON CONFLICT (id) DO UPDATE SET project_name=$2,project_desc=$3 RETURNING *;
                  """;
        return _host.QueryFirst(sql, MapRedMineProject, ("$1", id), ("$2", title), ("$3", description)) ?? new RedMineProject();
    }

    // $1=id $2=closed
    public void UpdateRedMineProjectStatus(int id, bool closed)
    {
        var sql = """
                  UPDATE redmine_projects SET is_closed=$2 WHERE id=$1;
                  """;
        _host.Execute(sql, ("$1", id), ("$2", closed ? 1 : 0));
    }

    // $1=work_id
    public WorkTimeEntry? WorkItemGetTimeEntry(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentNullException(nameof(item.Id));
        var sql = """
                  SELECT * FROM redmine_time_entries WHERE work_id=$1;
                  """;
        return _host.QueryFirst(sql, MapWorkTimeEntry, ("$1", item.Id));
    }

    public IDictionary<int, WorkTimeEntry> GetWorkTimeEntriesByDate(string date)
    {
        const string sql = """
                           SELECT redmine_time_entries.*
                           FROM redmine_time_entries INNER JOIN work_items ON redmine_time_entries.work_id = work_items.id
                           WHERE work_items.create_date = $1;
                           """;
        var result = new Dictionary<int, WorkTimeEntry>();
        foreach (var entry in _host.Query(sql, MapWorkTimeEntry, ("$1", date)))
            result[entry.WorkId] = entry;
        return result;
    }

    // $1=work_id
    public bool WorkItemWasUploaded(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentNullException(nameof(item.Id));
        var sql = """
                  SELECT * FROM redmine_time_entries WHERE work_id=$1 AND id>0;
                  """;
        return _host.Exists(sql, ("$1", item.Id));
    }

    public ICollection<RedMineActivity> GetRedMineActivities()
    {
        const string sql = """
                           SELECT * FROM redmine_activities;
                           """;
        return _host.Query(sql, MapRedMineActivity);
    }

    // $1=project_id（仅按项目过滤的分支用）
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
                      SELECT redmine_issues.id,redmine_issues.issue_title,redmine_issues.assigned_to,redmine_projects.project_name,redmine_issues.is_closed
                      FROM redmine_issues INNER JOIN redmine_projects ON redmine_issues.project_id = redmine_projects.id
                      ORDER BY redmine_issues.is_closed, redmine_issues.id DESC;
                      """;
            return _host.Query(sql, MapDisplay);
        }

        {
            var sql = """
                      SELECT redmine_issues.id,redmine_issues.issue_title,redmine_issues.assigned_to,redmine_projects.project_name,redmine_issues.is_closed
                      FROM redmine_issues INNER JOIN redmine_projects ON redmine_issues.project_id = redmine_projects.id AND redmine_issues.project_id=$1
                      ORDER BY redmine_issues.is_closed, redmine_issues.id DESC;
                      """;
            return _host.Query(sql, MapDisplay, ("$1", project.Id));
        }
    }

    public ICollection<RedMineProject> GetRedMineProjects()
    {
        const string sql = """
                           SELECT * FROM redmine_projects ORDER BY id DESC;
                           """;
        return _host.Query(sql, MapRedMineProject);
    }

    // $1=work $2=activity $3=issue
    public WorkTimeEntry? CreateWorkTimeEntry(int work, int activity, int issus)
    {
        try
        {
            var sql = """
                      INSERT INTO redmine_time_entries(work_id, act_id, issue_id) VALUES ($1, $2, $3)
                      ON CONFLICT (work_id) DO UPDATE SET act_id=$2, issue_id=$3 RETURNING *;
                      """;
            return _host.QueryFirst(sql, MapWorkTimeEntry, ("$1", work), ("$2", activity), ("$3", issus));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // $1=entryId $2=actId $3=issueId $4=workId
    public bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry)
    {
        if (timeEntry.WorkId == 0)
            throw new ArgumentException("Work time entry must have a valid id");
        var sql = """
                  UPDATE redmine_time_entries SET id=$1,act_id=$2,issue_id=$3 WHERE work_id=$4;
                  """;
        return _host.Execute(sql,
            ("$1", timeEntry.EntryId), ("$2", timeEntry.ActivityId),
            ("$3", timeEntry.IssueId), ("$4", timeEntry.WorkId)) > 0;
    }

    // ---- mappers（与迁出前一致；用 _host.ReadString 封装 CHAR padding，Pg 端 TrimEnd）----

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
