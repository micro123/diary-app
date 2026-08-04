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
public sealed class PgRedMineDb(IDbExtensionHost host, string instanceId) : IRedMineDb
{
    private const uint CurrentSchemaVersion = 2;
    private readonly IDbExtensionHost _host = host;

    public string InstanceId => instanceId;

    public uint SchemaVersion => CurrentSchemaVersion;

    public bool Initialize()
        => Initialize(new RedMinePlugin().GetMigrations().ToArray());

    public bool Initialize(IReadOnlyList<IPluginMigration> migrations)
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
                "PostgreSQL", 0, _host.ExecRaw,
                (sql, map, args) => _host.Query(sql, reader => map(reader),
                    args.Cast<(string Name, object? Value)>().ToArray()));
            if (!PluginMigrationRunner.Upgrade(
                    RedMinePluginConstants.PluginId, schemaVersion, CurrentSchemaVersion,
                    migrations, context))
            {
                return false;
            }

            return _host.ExecRaw(
                $"INSERT INTO plugin_data_versions(plugin_id, schema_version) VALUES ('{RedMinePluginConstants.PluginId}', 2) ON CONFLICT(plugin_id) DO UPDATE SET schema_version=2;");
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
                  INSERT INTO redmine_activities(instance_id,id, act_name) VALUES ($1,$2,$3) ON CONFLICT (instance_id,id) DO UPDATE SET act_name=$3 RETURNING id,act_name;
                  """;
        return _host.QueryFirst(sql, MapRedMineActivity, ("$1", InstanceId), ("$2", id), ("$3", title)) ?? new RedMineActivity();
    }

    public bool ClearData()
    {
        try
        {
            return _host.Execute("DELETE FROM redmine_time_entries WHERE instance_id=$1;", ("$1", InstanceId)) >= 0
                && _host.Execute("DELETE FROM redmine_activities WHERE instance_id=$1;", ("$1", InstanceId)) >= 0
                && _host.Execute("DELETE FROM redmine_issues WHERE instance_id=$1;", ("$1", InstanceId)) >= 0
                && _host.Execute("DELETE FROM redmine_projects WHERE instance_id=$1;", ("$1", InstanceId)) >= 0;
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
            ("$1", RedMinePluginConstants.PluginId));
        return value is null ? 0 : Convert.ToUInt32(value);
    }

    private bool HasLegacyTables()
        => Convert.ToBoolean(_host.ExecuteScalar(
            "SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_name=$1);",
            ("$1", "redmine_issues")));

    private bool HasInstanceColumn(string table)
        => Convert.ToBoolean(_host.ExecuteScalar(
            "SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name=$1 AND column_name=$2);",
            ("$1", table), ("$2", "instance_id")));

    // $1=id $2=title $3=assign $4=project $5=close
    public RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project,
        bool closed = false)
    {
        var sql = """
                  INSERT INTO redmine_issues(instance_id,id, issue_title, assigned_to, project_id, is_closed)
                  VALUES ($1,$2,$3,$4,$5,$6) ON CONFLICT(instance_id,id) DO UPDATE SET
                  issue_title=$3,assigned_to=$4,project_id=$5,is_closed=$6 RETURNING id,issue_title,assigned_to,project_id,is_closed;
                  """;
        return _host.QueryFirst(sql, MapRedMineIssue,
            ("$1", InstanceId), ("$2", id), ("$3", title), ("$4", assignedTo),
            ("$5", project), ("$6", closed ? 1 : 0)) ?? new RedMineIssue();
    }

    // $1=id $2=closed
    public void UpdateRedMineIssueStatus(int id, bool closed)
    {
        var sql = """
                  UPDATE redmine_issues SET is_closed=$3 WHERE instance_id=$1 AND id=$2;
                  """;
        _host.Execute(sql, ("$1", InstanceId), ("$2", id), ("$3", closed ? 1 : 0));
    }

    // $1=id $2=title $3=desc
    public RedMineProject AddRedMineProject(int id, string title, string description)
    {
        var sql = """
                  INSERT INTO redmine_projects(instance_id,id, project_name, project_desc)
                  VALUES ($1,$2,$3,$4) ON CONFLICT (instance_id,id) DO UPDATE SET project_name=$3,project_desc=$4 RETURNING id,project_name,project_desc,is_closed;
                  """;
        return _host.QueryFirst(sql, MapRedMineProject, ("$1", InstanceId), ("$2", id), ("$3", title), ("$4", description)) ?? new RedMineProject();
    }

    // $1=id $2=closed
    public void UpdateRedMineProjectStatus(int id, bool closed)
    {
        var sql = """
                  UPDATE redmine_projects SET is_closed=$3 WHERE instance_id=$1 AND id=$2;
                  """;
        _host.Execute(sql, ("$1", InstanceId), ("$2", id), ("$3", closed ? 1 : 0));
    }

    // $1=work_id
    public WorkTimeEntry? WorkItemGetTimeEntry(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentNullException(nameof(item.Id));
        var sql = """
                  SELECT work_id,id,act_id,issue_id FROM redmine_time_entries WHERE instance_id=$1 AND work_id=$2;
                  """;
        return _host.QueryFirst(sql, MapWorkTimeEntry, ("$1", InstanceId), ("$2", item.Id));
    }

    public IDictionary<int, WorkTimeEntry> GetWorkTimeEntriesByDate(string date)
    {
        const string sql = """
                           SELECT redmine_time_entries.work_id,redmine_time_entries.id,redmine_time_entries.act_id,redmine_time_entries.issue_id
                           FROM redmine_time_entries INNER JOIN work_items ON redmine_time_entries.work_id = work_items.id
                           WHERE redmine_time_entries.instance_id=$1 AND work_items.create_date = $2;
                           """;
        var result = new Dictionary<int, WorkTimeEntry>();
        foreach (var entry in _host.Query(sql, MapWorkTimeEntry, ("$1", InstanceId), ("$2", date)))
            result[entry.WorkId] = entry;
        return result;
    }

    // $1=work_id
    public bool WorkItemWasUploaded(WorkItem item)
    {
        if (item.Id == 0)
            throw new ArgumentNullException(nameof(item.Id));
        var sql = """
                  SELECT work_id,id,act_id,issue_id FROM redmine_time_entries WHERE instance_id=$1 AND work_id=$2 AND id>0;
                  """;
        return _host.Exists(sql, ("$1", InstanceId), ("$2", item.Id));
    }

    public ICollection<RedMineActivity> GetRedMineActivities()
    {
        const string sql = """
                           SELECT id,act_name FROM redmine_activities WHERE instance_id=$1;
                           """;
        return _host.Query(sql, MapRedMineActivity, ("$1", InstanceId));
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
                       FROM redmine_issues INNER JOIN redmine_projects ON redmine_issues.instance_id=redmine_projects.instance_id AND redmine_issues.project_id = redmine_projects.id
                       WHERE redmine_issues.instance_id=$1
                      ORDER BY redmine_issues.is_closed, redmine_issues.id DESC;
                      """;
            return _host.Query(sql, MapDisplay, ("$1", InstanceId));
        }

        {
            var sql = """
                      SELECT redmine_issues.id,redmine_issues.issue_title,redmine_issues.assigned_to,redmine_projects.project_name,redmine_issues.is_closed
                       FROM redmine_issues INNER JOIN redmine_projects ON redmine_issues.instance_id=redmine_projects.instance_id AND redmine_issues.project_id = redmine_projects.id AND redmine_issues.project_id=$2
                       WHERE redmine_issues.instance_id=$1
                      ORDER BY redmine_issues.is_closed, redmine_issues.id DESC;
                      """;
            return _host.Query(sql, MapDisplay, ("$1", InstanceId), ("$2", project.Id));
        }
    }

    public ICollection<RedMineProject> GetRedMineProjects()
    {
        const string sql = """
                           SELECT id,project_name,project_desc,is_closed FROM redmine_projects WHERE instance_id=$1 ORDER BY id DESC;
                           """;
        return _host.Query(sql, MapRedMineProject, ("$1", InstanceId));
    }

    // $1=work $2=activity $3=issue
    public WorkTimeEntry? CreateWorkTimeEntry(int work, int activity, int issus)
    {
        try
        {
            var sql = """
                      INSERT INTO redmine_time_entries(instance_id,work_id, act_id, issue_id) VALUES ($1, $2, $3, $4)
                      ON CONFLICT (instance_id,work_id) DO UPDATE SET act_id=$3, issue_id=$4 RETURNING work_id,id,act_id,issue_id;
                      """;
            return _host.QueryFirst(sql, MapWorkTimeEntry, ("$1", InstanceId), ("$2", work), ("$3", activity), ("$4", issus));
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
                  UPDATE redmine_time_entries SET id=$1,act_id=$2,issue_id=$3 WHERE instance_id=$5 AND work_id=$4;
                  """;
        return _host.Execute(sql,
            ("$1", timeEntry.EntryId), ("$2", timeEntry.ActivityId),
            ("$3", timeEntry.IssueId), ("$4", timeEntry.WorkId), ("$5", InstanceId)) > 0;
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
