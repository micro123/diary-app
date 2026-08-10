using System.Data.Common;
using System.Globalization;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.Jira;
using Diary.PluginBase;

namespace Diary.Db.SQLite;

public sealed class SQLiteJiraDb(IDbExtensionHost host, string instanceId) : IJiraDb
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
            var version = Convert.ToUInt32(_host.ExecuteScalar("SELECT COALESCE(schema_version,0) FROM plugin_data_versions WHERE plugin_id=$pluginId;", ("$pluginId", JiraPluginConstants.PluginId)) ?? 0);
            var context = new DelegatePluginMigrationContext("SQLite", 0, _host.ExecRaw, (sql, map, args) => _host.Query(sql, reader => map(reader), args.Cast<(string Name, object? Value)>().ToArray()));
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
        => _host.Execute("INSERT INTO jira_projects(instance_id,project_key,project_name,project_desc,is_archived) VALUES ($instanceId,$key,$name,$description,$archived) ON CONFLICT(instance_id,project_key) DO UPDATE SET project_name=$name,project_desc=$description,is_archived=$archived;", ("$instanceId", InstanceId), ("$key", project.Key), ("$name", project.Name), ("$description", project.Description), ("$archived", project.Archived ? 1 : 0));

    public void UpsertIssue(JiraIssue issue)
    {
        UpsertProject(new JiraProject(issue.ProjectKey, issue.ProjectName, string.Empty, false));
        _host.Execute("INSERT INTO jira_issues(instance_id,issue_key,issue_title,project_key,project_name,status_name,is_closed) VALUES ($instanceId,$key,$title,$projectKey,$projectName,$status,$closed) ON CONFLICT(instance_id,issue_key) DO UPDATE SET issue_title=$title,project_key=$projectKey,project_name=$projectName,status_name=$status,is_closed=$closed;", ("$instanceId", InstanceId), ("$key", issue.Key), ("$title", issue.Summary), ("$projectKey", issue.ProjectKey), ("$projectName", issue.ProjectName), ("$status", issue.Status), ("$closed", issue.Closed ? 1 : 0));
    }

    public ICollection<JiraIssueDisplay> GetIssues(bool openOnly = true)
    {
        var sql = "SELECT issue_key,issue_title,project_name,status_name,is_closed FROM jira_issues WHERE instance_id=$instanceId" + (openOnly ? " AND is_closed=0" : string.Empty) + " ORDER BY is_closed,issue_key;";
        return _host.Query(sql, MapIssue, ("$instanceId", InstanceId));
    }

    public ICollection<JiraProject> GetProjects()
        => _host.Query("SELECT project_key,project_name,project_desc,is_archived FROM jira_projects WHERE instance_id=$instanceId ORDER BY project_key;", MapProject, ("$instanceId", InstanceId));

    public JiraWorkTimeEntry? WorkItemGetTimeEntry(WorkItem item)
        => _host.QueryFirst("SELECT work_id,issue_key,remote_worklog_id,upload_state,upload_error,upload_attempted_at FROM jira_work_entries WHERE instance_id=$instanceId AND work_id=$workId;", MapEntry, ("$instanceId", InstanceId), ("$workId", item.Id));

    public IDictionary<int, JiraWorkTimeEntry> GetWorkTimeEntriesByDate(string date)
        => _host.Query("SELECT jira_work_entries.work_id,issue_key,remote_worklog_id,upload_state,upload_error,upload_attempted_at FROM jira_work_entries INNER JOIN work_items ON jira_work_entries.work_id=work_items.id WHERE jira_work_entries.instance_id=$instanceId AND work_items.create_date=$date;", MapEntry, ("$instanceId", InstanceId), ("$date", date)).ToDictionary(item => item.WorkId);

    public JiraWorkTimeEntry? CreateWorkTimeEntry(int workId, string issueKey)
    {
        _host.Execute("INSERT INTO jira_work_entries(instance_id,work_id,issue_key,remote_worklog_id) VALUES ($instanceId,$workId,$issueKey,NULL) ON CONFLICT(instance_id,work_id) DO UPDATE SET issue_key=$issueKey;", ("$instanceId", InstanceId), ("$workId", workId), ("$issueKey", issueKey));
        return WorkItemGetTimeEntry(new WorkItem { Id = workId });
    }

    public bool UpdateWorkTimeEntry(JiraWorkTimeEntry entry)
        => _host.Execute("UPDATE jira_work_entries SET issue_key=$issueKey,remote_worklog_id=$remoteId,upload_state=$state,upload_error=$error,upload_attempted_at=$attemptedAt WHERE instance_id=$instanceId AND work_id=$workId;", ("$instanceId", InstanceId), ("$workId", entry.WorkId), ("$issueKey", entry.IssueKey), ("$remoteId", (object?)entry.RemoteWorklogId ?? DBNull.Value), ("$state", entry.UploadState.ToString()), ("$error", (object?)entry.UploadError ?? DBNull.Value), ("$attemptedAt", (object?)entry.UploadAttemptedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value)) > 0;

    public bool WorkItemWasUploaded(WorkItem item)
        => _host.Exists("SELECT 1 FROM jira_work_entries WHERE instance_id=$instanceId AND work_id=$workId AND remote_worklog_id IS NOT NULL AND remote_worklog_id<>'';", ("$instanceId", InstanceId), ("$workId", item.Id));

    public bool ClearData()
        => _host.Execute("DELETE FROM jira_work_entries WHERE instance_id=$instanceId;", ("$instanceId", InstanceId)) >= 0
        && _host.Execute("DELETE FROM jira_issues WHERE instance_id=$instanceId;", ("$instanceId", InstanceId)) >= 0
        && _host.Execute("DELETE FROM jira_projects WHERE instance_id=$instanceId;", ("$instanceId", InstanceId)) >= 0;

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
