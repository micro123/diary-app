using Diary.PluginBase;

namespace Diary.Jira;

public sealed class JiraInitialMigration : IPluginMigration
{
    public string PluginId => JiraPluginConstants.PluginId;
    public uint FromVersion { get; init; } = 0;
    public uint ToVersion { get; init; } = 1;

    public bool Up(IPluginMigrationContext context)
    {
        const string sql = """
                           CREATE TABLE IF NOT EXISTS jira_projects(
                               instance_id TEXT NOT NULL,
                               project_key TEXT NOT NULL,
                               project_name TEXT NOT NULL,
                               project_desc TEXT NOT NULL DEFAULT '',
                               is_archived INTEGER NOT NULL DEFAULT 0,
                               PRIMARY KEY (instance_id, project_key)
                           );
                           CREATE TABLE IF NOT EXISTS jira_issues(
                               instance_id TEXT NOT NULL,
                               issue_key TEXT NOT NULL,
                               issue_title TEXT NOT NULL,
                               project_key TEXT NOT NULL,
                               project_name TEXT NOT NULL DEFAULT '',
                               status_name TEXT NOT NULL DEFAULT '',
                               is_closed INTEGER NOT NULL DEFAULT 0,
                               PRIMARY KEY (instance_id, issue_key),
                               FOREIGN KEY (instance_id, project_key)
                                   REFERENCES jira_projects(instance_id, project_key) ON DELETE CASCADE
                           );
                           CREATE TABLE IF NOT EXISTS jira_work_entries(
                               instance_id TEXT NOT NULL,
                               work_id INTEGER NOT NULL,
                               issue_key TEXT NOT NULL,
                               remote_worklog_id TEXT,
                               PRIMARY KEY (instance_id, work_id),
                               FOREIGN KEY (work_id) REFERENCES work_items(id) ON DELETE CASCADE,
                               FOREIGN KEY (instance_id, issue_key)
                                   REFERENCES jira_issues(instance_id, issue_key) ON DELETE CASCADE
                           );
                           CREATE INDEX IF NOT EXISTS idx_jira_issues_status
                               ON jira_issues(instance_id, is_closed, issue_key);
                           """;
        return context.ExecRaw(sql);
    }
}
