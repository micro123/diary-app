using Diary.Db.SQLite;
using Diary.Db.PostgreSQL;
using Diary.Database;
using Diary.PluginBase;
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
        var migrations = new RedMinePlugin().GetMigrations().ToArray();
        var company = db.GetExtension<IRedMineDb>("redmine.company", migrations);
        var personal = db.GetExtension<IRedMineDb>("redmine.personal", migrations);

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

    [TestMethod]
    public void SQLite_MigrationFailureThrowsAndLeavesNoVersionRow()
    {
        using var db = TestDb.Create();
        var migrations = new IPluginMigration[]
        {
            new RedMineInitialMigration(),
            new RedMineInstanceMigration(),
            new FailingMigration(),
        };

        // 失败应抛 PluginExtensionInitException，且不缓存（重试仍可触达）
        try
        {
            db.GetExtension<IRedMineDb>("redmine.failing", migrations);
            Assert.Fail("Expected PluginExtensionInitException was not thrown.");
        }
        catch (PluginExtensionInitException)
        {
            // expected
        }

        // 版本表未写入 schema_version=3
        Assert.IsFalse(((IDbExtensionHost)db).Exists("SELECT 1 FROM plugin_data_versions WHERE schema_version = 3;"));
    }

    [TestMethod]
    public void SQLite_FreshDb_UpgradesToLatestVersion()
    {
        using var db = TestDb.Create();
        var extension = db.GetExtension<IRedMineDb>(
            "redmine.default", new RedMinePlugin().GetMigrations());

        Assert.IsNotNull(extension);
        Assert.AreEqual(2u, extension!.GetSchemaVersion());
        Assert.IsTrue(((IDbExtensionHost)db).Exists(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='redmine_issues';"));
        Assert.IsTrue(((IDbExtensionHost)db).Exists(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='redmine_time_entries';"));
        Assert.IsTrue(((IDbExtensionHost)db).Exists(
            "SELECT 1 FROM plugin_data_versions WHERE schema_version = 2;"));
    }

    [TestMethod]
    public void SQLite_ExtensionInstanceIdMatchesRequest()
    {
        using var db = TestDb.Create();
        var migrations = new RedMinePlugin().GetMigrations();
        var company = db.GetExtension<IRedMineDb>("redmine.company", migrations);
        var personal = db.GetExtension<IRedMineDb>("redmine.personal", migrations);

        Assert.IsNotNull(company);
        Assert.IsNotNull(personal);
        Assert.AreEqual("redmine.company", company!.InstanceId);
        Assert.AreEqual("redmine.personal", personal!.InstanceId);
    }

    [TestMethod]
    public void SQLite_FailureNotCached_RetryStillThrows()
    {
        using var db = TestDb.Create();
        var migrations = new IPluginMigration[]
        {
            new RedMineInitialMigration(),
            new RedMineInstanceMigration(),
            new FailingMigration(),
        };

        for (var i = 0; i < 2; i++)
        {
            try
            {
                db.GetExtension<IRedMineDb>("redmine.failing", migrations);
                Assert.Fail($"第 {i + 1} 次调用应抛 PluginExtensionInitException。");
            }
            catch (PluginExtensionInitException)
            {
                // 每次都应重新触达迁移，不缓存失败
            }
        }
    }

    [TestMethod]
    public void SQLite_NullResultCachedForUnsupportedType()
    {
        using var db = TestDb.Create();

        // 无工厂支持 UnknownExtension 类型 → 返回 null 并缓存
        Assert.IsNull(db.GetExtension<UnknownExtension>("x", null));
        Assert.IsNull(db.GetExtension<UnknownExtension>("x", null));
    }

    [TestMethod]
    public void PostgreSql_InitializationIsIdempotent()
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
        var migrations = new RedMinePlugin().GetMigrations().ToArray();
        var extension = new PgRedMineDb(db, "redmine.default");

        Assert.IsTrue(extension.Initialize(migrations));
        Assert.IsTrue(extension.AddRedMineProject(7, "Project", "").Id == 7);
        Assert.IsTrue(extension.Initialize(migrations));

        Assert.AreEqual(1, extension.GetRedMineProjects().Count);
        Assert.AreEqual(2u, extension.GetSchemaVersion());
    }

    [TestMethod]
    public void PostgreSql_MigrationFailureThrowsAndLeavesNoVersionRow()
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
        // 容器 DB 在测试间持久：先清掉前序测试遗留的 RedMine 表与版本行，
        // 使本测试从 schema 0 起步，确保失败迁移真正被调度。
        Assert.IsTrue(db.ExecRaw(
            "DROP TABLE IF EXISTS redmine_time_entries, redmine_issues, redmine_activities, redmine_projects, plugin_data_versions CASCADE;"));
        var migrations = new IPluginMigration[]
        {
            new RedMineInitialMigration(),
            new RedMineInstanceMigration(),
            new FailingMigration(),
        };

        try
        {
            db.GetExtension<IRedMineDb>("redmine.failing", migrations);
            Assert.Fail("Expected PluginExtensionInitException was not thrown.");
        }
        catch (PluginExtensionInitException)
        {
            // expected
        }

        Assert.IsFalse(((IDbExtensionHost)db).Exists(
            "SELECT 1 FROM plugin_data_versions WHERE schema_version = 3;"));
    }

    private sealed class FailingMigration : IPluginMigration
    {
        public string PluginId => RedMinePluginConstants.PluginId;
        public uint FromVersion { get; init; } = 2;
        public uint ToVersion { get; init; } = 3;
        public bool Up(IPluginMigrationContext context) => false;
    }

    private sealed class UnknownExtension;
}
