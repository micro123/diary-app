using Diary.Db.SQLite;
using Diary.Db.PostgreSQL;
using Diary.RedMine;

namespace Diary.DbTests;

[TestClass]
public sealed class RedMineMigrationTests
{
    [TestMethod]
    public void SQLite_RepairsVersionTwoDatabaseWithoutInstanceColumns()
    {
        using var db = TestDb.Create();
        Assert.IsTrue(db.ExecRaw("""
            CREATE TABLE plugin_data_versions(plugin_id CHAR(128) PRIMARY KEY, schema_version INTEGER NOT NULL);
            CREATE TABLE redmine_projects(id INTEGER PRIMARY KEY, project_name CHAR(256) NOT NULL, project_desc CHAR(2048) DEFAULT '', is_closed INTEGER DEFAULT 0);
            CREATE TABLE redmine_activities(id INTEGER PRIMARY KEY, act_name CHAR(64) NOT NULL);
            CREATE TABLE redmine_issues(id INTEGER PRIMARY KEY, issue_title CHAR(256) NOT NULL, assigned_to CHAR(16) DEFAULT '', project_id INTEGER NOT NULL, is_closed INTEGER DEFAULT 0);
            CREATE TABLE redmine_time_entries(work_id INTEGER PRIMARY KEY, id INTEGER DEFAULT 0, act_id INTEGER, issue_id INTEGER);
            INSERT INTO plugin_data_versions VALUES ('tracker.redmine', 2);
            INSERT INTO redmine_projects VALUES (7, 'Legacy project', '', 0);
            INSERT INTO redmine_issues VALUES (42, 'Legacy issue', 'user', 7, 0);
            """));

        Assert.IsTrue(db.ExecRaw("INSERT INTO work_items(id, create_date, comment, hours, priority) VALUES (1, '2026-01-01', 'test', 1, 0);"));
        var extension = new SQLiteRedMineDb(db, "redmine.default");

        Assert.IsTrue(extension.Initialize(new RedMinePlugin().GetMigrations().ToArray()));
        Assert.AreEqual(1, extension.GetRedMineProjects().Count);
        Assert.AreEqual(1, extension.GetRedMineIssues(null).Count);
        Assert.AreEqual(2u, extension.GetSchemaVersion());
    }

    [TestMethod]
    public void SQLite_InitializationIsIdempotent()
    {
        using var db = TestDb.Create();
        var extension = new SQLiteRedMineDb(db, "redmine.default");

        Assert.IsTrue(extension.Initialize(new RedMinePlugin().GetMigrations().ToArray()));
        Assert.IsTrue(extension.AddRedMineProject(7, "Project", "").Id == 7);
        Assert.IsTrue(extension.Initialize(new RedMinePlugin().GetMigrations().ToArray()));

        Assert.AreEqual(1, extension.GetRedMineProjects().Count);
        Assert.AreEqual(2u, extension.GetSchemaVersion());
    }

    [TestMethod]
    public void SQLite_MultipleInstances_IsolateSameRemoteIds()
    {
        using var db = TestDb.Create();
        var company = db.GetExtension<IRedMineDb>("redmine.company");
        var personal = db.GetExtension<IRedMineDb>("redmine.personal");

        Assert.IsNotNull(company);
        Assert.IsNotNull(personal);
        Assert.AreNotSame(company, personal);

        company!.AddRedMineProject(7, "Company", "");
        personal!.AddRedMineProject(7, "Personal", "");

        Assert.AreEqual("Company", company.GetRedMineProjects().Single().Title);
        Assert.AreEqual("Personal", personal.GetRedMineProjects().Single().Title);
    }

    [TestMethod]
    public void PostgreSql_RepairsVersionTwoDatabaseWithoutInstanceColumns()
    {
        var factory = PgContainerFixture.CreateFactory();
        if (factory is null)
        {
            Assert.Inconclusive("PostgreSQL 容器不可用（Docker 未运行？）");
            return;
        }

        using var db = factory.Create();
        Assert.IsTrue(db.Connect());
        Assert.IsTrue(db.Initialized());
        Assert.IsTrue(db.ExecRaw("""
            DROP TABLE IF EXISTS redmine_time_entries, redmine_issues, redmine_activities, redmine_projects, plugin_data_versions CASCADE;
            CREATE TABLE plugin_data_versions(plugin_id CHAR(128) PRIMARY KEY, schema_version INTEGER NOT NULL);
            CREATE TABLE redmine_projects(id INTEGER PRIMARY KEY, project_name CHAR(256) NOT NULL, project_desc CHAR(2048) DEFAULT '', is_closed INTEGER DEFAULT 0);
            CREATE TABLE redmine_activities(id INTEGER PRIMARY KEY, act_name CHAR(64) NOT NULL);
            CREATE TABLE redmine_issues(id INTEGER PRIMARY KEY, issue_title CHAR(256) NOT NULL, assigned_to CHAR(16) DEFAULT '', project_id INTEGER NOT NULL, is_closed INTEGER DEFAULT 0);
            CREATE TABLE redmine_time_entries(work_id INTEGER PRIMARY KEY, id INTEGER DEFAULT 0, act_id INTEGER, issue_id INTEGER);
            INSERT INTO plugin_data_versions VALUES ('tracker.redmine', 2);
            INSERT INTO redmine_projects VALUES (7, 'Legacy project', '', 0);
            INSERT INTO redmine_issues VALUES (42, 'Legacy issue', 'user', 7, 0);
            """));

        var extension = new PgRedMineDb(db, "redmine.default");

        Assert.IsTrue(extension.Initialize(new RedMinePlugin().GetMigrations().ToArray()));
        Assert.AreEqual(1, extension.GetRedMineProjects().Count);
        Assert.AreEqual(2u, extension.GetSchemaVersion());
    }
}
