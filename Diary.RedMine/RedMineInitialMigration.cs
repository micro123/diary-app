using Diary.PluginBase;

namespace Diary.RedMine;

public sealed class RedMineInitialMigration : IPluginMigration
{
    public string PluginId => "tracker.redmine";
    public uint FromVersion { get; init; } = 0;
    public uint ToVersion { get; init; } = 2;

    public bool Up(IPluginMigrationContext context)
    {
        const string sql = """
                           CREATE TABLE IF NOT EXISTS redmine_projects(
                               instance_id CHAR(128) NOT NULL,
                               id INTEGER NOT NULL,
                               project_name CHAR(256) NOT NULL,
                               project_desc CHAR(2048) DEFAULT '',
                               is_closed INTEGER DEFAULT 0,
                               PRIMARY KEY (instance_id, id)
                           );
                           CREATE TABLE IF NOT EXISTS redmine_activities(
                               instance_id CHAR(128) NOT NULL,
                               id INTEGER NOT NULL,
                               act_name CHAR(64) NOT NULL,
                               PRIMARY KEY (instance_id, id)
                           );
                           CREATE TABLE IF NOT EXISTS redmine_issues(
                               instance_id CHAR(128) NOT NULL,
                               id INTEGER NOT NULL,
                               issue_title CHAR(256) NOT NULL,
                               assigned_to CHAR(16) DEFAULT '',
                               project_id INTEGER NOT NULL,
                               is_closed INTEGER default 0,
                               PRIMARY KEY (instance_id, id),
                               FOREIGN KEY (instance_id, project_id)
                                   REFERENCES redmine_projects(instance_id, id) ON DELETE CASCADE
                           );
                           CREATE TABLE IF NOT EXISTS redmine_time_entries(
                               instance_id CHAR(128) NOT NULL,
                               work_id INTEGER NOT NULL,
                               id INTEGER DEFAULT 0,
                               act_id INTEGER,
                               issue_id INTEGER,
                               PRIMARY KEY (instance_id, work_id),
                               FOREIGN KEY (work_id) REFERENCES work_items(id) ON DELETE CASCADE,
                               FOREIGN KEY (instance_id, act_id)
                                   REFERENCES redmine_activities(instance_id, id) ON DELETE CASCADE,
                               FOREIGN KEY (instance_id, issue_id)
                                   REFERENCES redmine_issues(instance_id, id) ON DELETE CASCADE
                           );
                           CREATE INDEX IF NOT EXISTS idx_redmine_issues_project
                               ON redmine_issues(instance_id, project_id);
                           """;
        return context.ExecRaw(sql);
    }
}
