using Diary.PluginBase;

namespace Diary.RedMine;

public sealed class RedMineInstanceMigration : IPluginMigration
{
    public string PluginId => RedMinePluginConstants.PluginId;
    public uint FromVersion { get; init; } = 1;
    public uint ToVersion { get; init; } = 2;

    public bool Up(IPluginMigrationContext context)
    {
        var sql = context.ProviderName.StartsWith("SQLite", StringComparison.OrdinalIgnoreCase)
            ? SQLiteMigration
            : PostgreSqlMigration;
        return context.ExecRaw(sql);
    }

    private const string SQLiteMigration = """
                                          ALTER TABLE redmine_projects ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                                          ALTER TABLE redmine_activities ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                                          ALTER TABLE redmine_issues ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                                          ALTER TABLE redmine_time_entries ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                                          PRAGMA foreign_keys=OFF;
                                          ALTER TABLE redmine_time_entries RENAME TO redmine_time_entries_v1;
                                          ALTER TABLE redmine_issues RENAME TO redmine_issues_v1;
                                          ALTER TABLE redmine_activities RENAME TO redmine_activities_v1;
                                          ALTER TABLE redmine_projects RENAME TO redmine_projects_v1;
                                          CREATE TABLE redmine_projects(
                                              instance_id CHAR(128) NOT NULL,
                                              id INTEGER NOT NULL,
                                              project_name CHAR(256) NOT NULL,
                                              project_desc CHAR(2048) DEFAULT '',
                                              is_closed INTEGER DEFAULT 0,
                                              PRIMARY KEY(instance_id,id)
                                          );
                                          CREATE TABLE redmine_activities(
                                              instance_id CHAR(128) NOT NULL,
                                              id INTEGER NOT NULL,
                                              act_name CHAR(64) NOT NULL,
                                              PRIMARY KEY(instance_id,id)
                                          );
                                          CREATE TABLE redmine_issues(
                                              instance_id CHAR(128) NOT NULL,
                                              id INTEGER NOT NULL,
                                              issue_title CHAR(256) NOT NULL,
                                              assigned_to CHAR(16) DEFAULT '',
                                              project_id INTEGER NOT NULL,
                                              is_closed INTEGER DEFAULT 0,
                                              PRIMARY KEY(instance_id,id),
                                              FOREIGN KEY(instance_id,project_id) REFERENCES redmine_projects(instance_id,id) ON DELETE CASCADE
                                          );
                                          CREATE TABLE redmine_time_entries(
                                              instance_id CHAR(128) NOT NULL,
                                              work_id INTEGER NOT NULL,
                                              id INTEGER DEFAULT 0,
                                              act_id INTEGER,
                                              issue_id INTEGER,
                                              PRIMARY KEY(instance_id,work_id),
                                              FOREIGN KEY(work_id) REFERENCES work_items(id) ON DELETE CASCADE,
                                              FOREIGN KEY(instance_id,act_id) REFERENCES redmine_activities(instance_id,id) ON DELETE CASCADE,
                                              FOREIGN KEY(instance_id,issue_id) REFERENCES redmine_issues(instance_id,id) ON DELETE CASCADE
                                          );
                                          INSERT INTO redmine_projects SELECT instance_id,id,project_name,project_desc,is_closed FROM redmine_projects_v1;
                                          INSERT INTO redmine_activities SELECT instance_id,id,act_name FROM redmine_activities_v1;
                                          INSERT INTO redmine_issues SELECT instance_id,id,issue_title,assigned_to,project_id,is_closed FROM redmine_issues_v1;
                                          INSERT INTO redmine_time_entries SELECT instance_id,work_id,id,act_id,issue_id FROM redmine_time_entries_v1;
                                          DROP TABLE redmine_time_entries_v1;
                                          DROP TABLE redmine_issues_v1;
                                          DROP TABLE redmine_activities_v1;
                                          DROP TABLE redmine_projects_v1;
                                          CREATE INDEX idx_redmine_issues_project ON redmine_issues(instance_id,project_id);
                                          PRAGMA foreign_keys=ON;
                                          """;

    private const string PostgreSqlMigration = """
                                               ALTER TABLE redmine_projects ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                                               ALTER TABLE redmine_activities ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                                               ALTER TABLE redmine_issues ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                                               ALTER TABLE redmine_time_entries ADD COLUMN instance_id CHAR(128) NOT NULL DEFAULT 'redmine.default';
                                               ALTER TABLE redmine_issues DROP CONSTRAINT IF EXISTS redmine_issues_project_id_fkey;
                                               ALTER TABLE redmine_time_entries DROP CONSTRAINT IF EXISTS redmine_time_entries_act_id_fkey;
                                               ALTER TABLE redmine_time_entries DROP CONSTRAINT IF EXISTS redmine_time_entries_issue_id_fkey;
                                               ALTER TABLE redmine_time_entries DROP CONSTRAINT IF EXISTS redmine_time_entries_pkey;
                                               ALTER TABLE redmine_issues DROP CONSTRAINT IF EXISTS redmine_issues_pkey;
                                               ALTER TABLE redmine_activities DROP CONSTRAINT IF EXISTS redmine_activities_pkey;
                                               ALTER TABLE redmine_projects DROP CONSTRAINT IF EXISTS redmine_projects_pkey;
                                               ALTER TABLE redmine_projects ADD PRIMARY KEY(instance_id,id);
                                               ALTER TABLE redmine_activities ADD PRIMARY KEY(instance_id,id);
                                               ALTER TABLE redmine_issues ADD PRIMARY KEY(instance_id,id);
                                               ALTER TABLE redmine_time_entries ADD PRIMARY KEY(instance_id,work_id);
                                               ALTER TABLE redmine_issues ADD CONSTRAINT redmine_issues_project_fkey FOREIGN KEY(instance_id,project_id) REFERENCES redmine_projects(instance_id,id) ON DELETE CASCADE;
                                               ALTER TABLE redmine_time_entries ADD CONSTRAINT redmine_time_entries_activity_fkey FOREIGN KEY(instance_id,act_id) REFERENCES redmine_activities(instance_id,id) ON DELETE CASCADE;
                                               ALTER TABLE redmine_time_entries ADD CONSTRAINT redmine_time_entries_issue_fkey FOREIGN KEY(instance_id,issue_id) REFERENCES redmine_issues(instance_id,id) ON DELETE CASCADE;
                                               CREATE INDEX IF NOT EXISTS idx_redmine_issues_project ON redmine_issues(instance_id,project_id);
                                               """;
}
