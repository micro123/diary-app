using Diary.PluginBase;

namespace Diary.RedMine;

public sealed class RedMineInstanceMigration : IPluginMigration
{
    public string PluginId => "tracker.redmine";
    public uint FromVersion { get; init; } = 1;
    public uint ToVersion { get; init; } = 2;

    public bool Up(IPluginMigrationContext context)
    {
        const string sql = """
                           ALTER TABLE redmine_projects ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                           ALTER TABLE redmine_activities ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                           ALTER TABLE redmine_issues ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                           ALTER TABLE redmine_time_entries ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                           CREATE UNIQUE INDEX IF NOT EXISTS idx_redmine_projects_instance_id ON redmine_projects(instance_id, id);
                           CREATE UNIQUE INDEX IF NOT EXISTS idx_redmine_activities_instance_id ON redmine_activities(instance_id, id);
                           CREATE UNIQUE INDEX IF NOT EXISTS idx_redmine_issues_instance_id ON redmine_issues(instance_id, id);
                           CREATE UNIQUE INDEX IF NOT EXISTS idx_redmine_time_entries_instance_id ON redmine_time_entries(instance_id, work_id);
                           """;
        return context.ExecRaw(sql);
    }
}
