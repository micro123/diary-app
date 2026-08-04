using Diary.PluginBase;

namespace Diary.RedMine;

public sealed class RedMineInitialMigration : IPluginMigration
{
    public string PluginId => "tracker.redmine";
    public uint FromVersion { get; init; } = 0;
    public uint ToVersion { get; init; } = 1;

    public bool Up(IPluginMigrationContext context)
    {
        const string sql = """
                           CREATE TABLE IF NOT EXISTS redmine_projects(
                               id INTEGER NOT NULL PRIMARY KEY,
                               project_name CHAR(256) NOT NULL,
                               project_desc CHAR(2048) DEFAULT '',
                               is_closed INTEGER DEFAULT 0
                           );
                           CREATE TABLE IF NOT EXISTS redmine_activities(
                               id INTEGER PRIMARY KEY,
                               act_name CHAR(64) NOT NULL
                           );
                           CREATE TABLE IF NOT EXISTS redmine_issues(
                               id INTEGER PRIMARY KEY,
                               issue_title CHAR(256) NOT NULL,
                               assigned_to CHAR(16) DEFAULT '',
                               project_id INTEGER NOT NULL REFERENCES redmine_projects(id) ON DELETE CASCADE,
                               is_closed INTEGER DEFAULT 0
                           );
                           CREATE TABLE IF NOT EXISTS redmine_time_entries(
                               work_id INTEGER PRIMARY KEY REFERENCES work_items(id) ON DELETE CASCADE,
                               id INTEGER DEFAULT 0,
                               act_id INTEGER REFERENCES redmine_activities(id) ON DELETE CASCADE,
                               issue_id INTEGER REFERENCES redmine_issues(id) ON DELETE CASCADE
                           );
                           CREATE INDEX IF NOT EXISTS idx_redmine_issues_project ON redmine_issues(project_id);
                           """;
        return context.ExecRaw(sql);
    }
}
