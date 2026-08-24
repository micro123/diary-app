using System.Data.Common;
using System.Globalization;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.Jira;
using Diary.PluginBase;

namespace Diary.Db.PostgreSQL;

public sealed class PgJiraDb(IDbExtensionHost host, string instanceId) : IJiraDb
{
    private const uint CurrentSchemaVersion = 1;
    private readonly IDbExtensionHost _host = host;
    public string InstanceId => instanceId;
    public uint SchemaVersion => CurrentSchemaVersion;

    public bool Initialize(IReadOnlyList<IPluginMigration> migrations, out string? error)
    {
        error = null;
        try
        {
            if (!_host.ExecRaw("CREATE TABLE IF NOT EXISTS plugin_data_versions(plugin_id CHAR(128) PRIMARY KEY, schema_version INTEGER NOT NULL);"))
            {
                error = "无法创建 plugin_data_versions 表";
                return false;
            }
            var version = Convert.ToUInt32(_host.ExecuteScalar("SELECT COALESCE(schema_version,0) FROM plugin_data_versions WHERE plugin_id=$1;", ("$1", JiraPluginConstants.PluginId)) ?? 0);
            var context = new DelegatePluginMigrationContext("PgDb", 0, _host.ExecRaw, (sql, map, args) => _host.Query(sql, reader => map(reader), args.Cast<(string Name, object? Value)>().ToArray()));
            if (!PluginMigrationRunner.Upgrade(JiraPluginConstants.PluginId, version, CurrentSchemaVersion, migrations, context, out error))
                return false;
            return _host.ExecRaw($"INSERT INTO plugin_data_versions(plugin_id, schema_version) VALUES ('{JiraPluginConstants.PluginId}',1) ON CONFLICT(plugin_id) DO UPDATE SET schema_version=1;");
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public void UpsertProject(JiraProject project)
        => _host.Execute("INSERT INTO jira_projects(instance_id,project_key,project_name,project_desc,is_archived) VALUES ($1,$2,$3,$4,$5) ON CONFLICT(instance_id,project_key) DO UPDATE SET project_name=$3,project_desc=$4,is_archived=$5;", ("$1", InstanceId), ("$2", project.Key), ("$3", project.Name), ("$4", project.Description), ("$5", project.Archived ? 1 : 0));

    public void UpsertIssue(JiraIssue issue)
    {
        UpsertProject(new JiraProject(issue.ProjectKey, issue.ProjectName, string.Empty, false));
        _host.Execute("INSERT INTO jira_issues(instance_id,issue_key,issue_title,project_key,project_name,status_name,is_closed) VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT(instance_id,issue_key) DO UPDATE SET issue_title=$3,project_key=$4,project_name=$5,status_name=$6,is_closed=$7;", ("$1", InstanceId), ("$2", issue.Key), ("$3", issue.Summary), ("$4", issue.ProjectKey), ("$5", issue.ProjectName), ("$6", issue.Status), ("$7", issue.Closed ? 1 : 0));
    }

    public ICollection<JiraIssueDisplay> GetIssues(bool openOnly = true)
    {
        var sql = "SELECT issue_key,issue_title,project_name,status_name,is_closed FROM jira_issues WHERE instance_id=$1" + (openOnly ? " AND is_closed=0" : string.Empty) + " ORDER BY is_closed,issue_key;";
        return _host.Query(sql, MapIssue, ("$1", InstanceId));
    }

    public ICollection<JiraProject> GetProjects()
        => _host.Query("SELECT project_key,project_name,project_desc,is_archived FROM jira_projects WHERE instance_id=$1 ORDER BY project_key;", MapProject, ("$1", InstanceId));

    public JiraWorkTimeEntry? WorkItemGetTimeEntry(WorkItem item)
        => _host.QueryFirst("SELECT work_id,issue_key,remote_worklog_id,upload_state,upload_error,upload_attempted_at FROM jira_work_entries WHERE instance_id=$1 AND work_id=$2;", MapEntry, ("$1", InstanceId), ("$2", item.Id));

    public IDictionary<int, JiraWorkTimeEntry> GetWorkTimeEntriesByDate(string date)
        => _host.Query("SELECT jira_work_entries.work_id,issue_key,remote_worklog_id,upload_state,upload_error,upload_attempted_at FROM jira_work_entries INNER JOIN work_items ON jira_work_entries.work_id=work_items.id WHERE jira_work_entries.instance_id=$1 AND work_items.create_date=$2;", MapEntry, ("$1", InstanceId), ("$2", date)).ToDictionary(item => item.WorkId);

    public JiraWorkTimeEntry? CreateWorkTimeEntry(int workId, string issueKey)
    {
        _host.Execute("INSERT INTO jira_work_entries(instance_id,work_id,issue_key,remote_worklog_id) VALUES ($1,$2,$3,NULL) ON CONFLICT(instance_id,work_id) DO UPDATE SET issue_key=$3;", ("$1", InstanceId), ("$2", workId), ("$3", issueKey));
        return WorkItemGetTimeEntry(new WorkItem { Id = workId });
    }

    public bool UpdateWorkTimeEntry(JiraWorkTimeEntry entry)
        => _host.Execute("UPDATE jira_work_entries SET issue_key=$3,remote_worklog_id=$4,upload_state=$5,upload_error=$6,upload_attempted_at=$7 WHERE instance_id=$1 AND work_id=$2;", ("$1", InstanceId), ("$2", entry.WorkId), ("$3", entry.IssueKey), ("$4", (object?)entry.RemoteWorklogId ?? DBNull.Value), ("$5", entry.UploadState.ToString()), ("$6", (object?)entry.UploadError ?? DBNull.Value), ("$7", (object?)entry.UploadAttemptedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value)) > 0;

    public bool WorkItemWasUploaded(WorkItem item)
        => _host.Exists("SELECT 1 FROM jira_work_entries WHERE instance_id=$1 AND work_id=$2 AND remote_worklog_id IS NOT NULL AND remote_worklog_id<>'';", ("$1", InstanceId), ("$2", item.Id));

    public bool ClearData()
        => _host.Execute("DELETE FROM jira_work_entries WHERE instance_id=$1;", ("$1", InstanceId)) >= 0
        && _host.Execute("DELETE FROM jira_issues WHERE instance_id=$1;", ("$1", InstanceId)) >= 0
        && _host.Execute("DELETE FROM jira_projects WHERE instance_id=$1;", ("$1", InstanceId)) >= 0;

    private JiraIssueDisplay MapIssue(DbDataReader reader) => new()
    {
        Key = _host.ReadString(reader, 0),
        Summary = _host.ReadString(reader, 1),
        Project = _host.ReadString(reader, 2),
        Status = _host.ReadString(reader, 3),
        Disabled = reader.GetInt32(4) != 0,
    };
    private JiraProject MapProject(DbDataReader reader) => new(_host.ReadString(reader, 0), _host.ReadString(reader, 1), _host.ReadString(reader, 2), reader.GetInt32(3) != 0);
    private JiraWorkTimeEntry MapEntry(DbDataReader reader) => new()
    {
        WorkId = reader.GetInt32(0),
        IssueKey = _host.ReadString(reader, 1),
        RemoteWorklogId = reader.IsDBNull(2) ? null : _host.ReadString(reader, 2),
        UploadState = ParseState(_host.ReadString(reader, 3)),
        UploadError = reader.IsDBNull(4) ? null : _host.ReadString(reader, 4),
        UploadAttemptedAt = ParseAttemptedAt(reader.IsDBNull(5) ? null : _host.ReadString(reader, 5)),
    };

    private static TrackerUploadState ParseState(string value)
        => Enum.TryParse<TrackerUploadState>(value, out var state) ? state : TrackerUploadState.NotAttempted;

    private static DateTimeOffset? ParseAttemptedAt(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
            ? result
            : null;
}
