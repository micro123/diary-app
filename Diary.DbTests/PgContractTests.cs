using Diary.Core;
using Diary.Database;
using Diary.Db.PostgreSQL;
using Npgsql;

namespace Diary.DbTests;

/// <summary>PostgreSQL（Testcontainers）跑全部契约场景。每测 new 一个 PgDb + DropData 清数据。</summary>
[TestClass]
public class PgContractTests : DbContractTests
{
    protected override DbInterfaceBase CreateDb(Func<uint, Migration?>? getMigration = null)
    {
        var factory = PgContainerFixture.CreateFactory(getMigration);
        if (factory is null)
        {
            Assert.Inconclusive("PostgreSQL 容器不可用（Docker 未运行？）");
            return null!;
        }
        var db = factory.Create();
        Assert.IsTrue(db.Connect(), "Pg Connect 失败");
        Assert.IsTrue(db.Initialized(), "Pg Initialized 失败");
        Assert.IsTrue(db.ExecRaw("DELETE FROM data_versions; INSERT INTO data_versions VALUES(65536);"),
            "Pg data_versions 重置失败");
        Assert.IsTrue(db.ExecRaw("DELETE FROM diary_schema_metadata; DELETE FROM diary_schema_migrations;"),
            "Pg 兼容性元数据重置失败");
        Assert.IsTrue(db.DropData(), "Pg DropData 失败（每测清空数据）");
        Assert.IsTrue(GetRedMine(db).ClearData(), "RedMine DropData 失败（每测清空数据）");
        return db;
    }

    [TestMethod]
    public void Compatibility_CaseInsensitiveDuplicateFieldKey_IsDataIntegrityError()
    {
        using var db = CreateDb();
        var tag = db.CreateWorkTag("integrity-tag", true, 0);
        Assert.IsTrue(db.ExecRaw(
            "DROP INDEX ux_tag_extra_fields_key; " +
            "CREATE UNIQUE INDEX ux_tag_extra_fields_key ON tag_extra_field_definitions(field_key);"));
        Assert.IsTrue(db.ExecRaw(
            $"INSERT INTO tag_extra_field_definitions " +
            $"(field_id, field_key, tag_id, label, field_type, description, sort_order, options_json, enabled) " +
            $"VALUES ('integrity-1', 'Duplicate.Key', {tag.Id}, 'A', 0, '', 0, '[]', TRUE), " +
            $"('integrity-2', 'duplicate.key', {tag.Id}, 'B', 0, '', 1, '[]', TRUE);"));

        try
        {
            var report = db.CheckCompatibility(DataVersion.VersionCode);
            Assert.AreEqual(DbCompatibilityState.DataIntegrityError, report.State, report.ToUserMessage());
            Assert.IsTrue(report.Issues.Any(issue => issue.Code == "DB-DATA-DUPLICATE-FIELD-KEY"));
        }
        finally
        {
            Assert.IsTrue(db.ExecRaw(
                "DELETE FROM tag_extra_field_definitions WHERE field_id IN ('integrity-1', 'integrity-2'); " +
                "DROP INDEX IF EXISTS ux_tag_extra_fields_key; " +
                "CREATE UNIQUE INDEX ux_tag_extra_fields_key " +
                "ON tag_extra_field_definitions(LOWER(field_key));"));
        }
    }

    [TestMethod]
    public void Maintenance_BackupAndRestoreCustomArchiveToFreshDatabase()
    {
        var sourceFactory = PgContainerFixture.CreateFactory();
        if (sourceFactory is null)
        {
            Assert.Inconclusive("PostgreSQL 容器不可用（Docker 未运行？）");
            return;
        }

        var sourceConfig = (Config)sourceFactory.GetConfig();
        using var source = (PgDb)sourceFactory.Create();
        Assert.IsTrue(source.Connect(), "源 PostgreSQL 连接失败");
        Assert.IsTrue(source.Initialized(), "源 PostgreSQL 初始化失败");
        Assert.IsTrue(source.DropData(), "源 PostgreSQL 清理失败");
        var tag = source.CreateWorkTag("maintenance-tag", true, 7);
        Assert.AreNotEqual(0, tag.Id);

        var tools = source.GetToolAvailability();
        if (!tools.Supported)
        {
            Assert.Inconclusive(tools.UnavailableReason ?? "PostgreSQL 工具不可用。");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"diary-pg-maintenance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var backupPath = Path.Combine(root, "diary.dump");
        var targetDatabase = $"diary_restore_{Guid.NewGuid():N}";
        try
        {
            var backup = ((IDbMaintenanceProvider)source).CreateBackup(backupPath);
            if (!backup.Success && backup.Error?.Contains("主版本", StringComparison.Ordinal) == true)
            {
                Assert.Inconclusive(backup.Error);
                return;
            }
            Assert.IsTrue(backup.Success, backup.Error);
            source.Dispose();

            var targetConfig = new Config
            {
                Host = sourceConfig.Host,
                Port = sourceConfig.Port,
                Database = targetDatabase,
                User = sourceConfig.User,
                Password = sourceConfig.Password,
                ToolsBinPath = sourceConfig.ToolsBinPath,
            };
            using var target = new PgDb(new TestPgFactory(targetConfig));
            var restore = ((IDbMaintenanceProvider)target).RestoreBackup(
                backupPath,
                DataVersion.VersionCode);
            if (!restore.Success && restore.Error?.Contains("主版本", StringComparison.Ordinal) == true)
            {
                Assert.Inconclusive(restore.Error);
                return;
            }
            Assert.IsTrue(restore.Success, restore.Error);
            Assert.IsFalse(restore.TargetPreviouslyExisted);

            Assert.IsTrue(target.Connect(), "还原目标连接失败");
            Assert.AreEqual("maintenance-tag", target.AllWorkTags().Single().Name);
        }
        finally
        {
            DropDatabase(sourceConfig, targetDatabase);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void DropDatabase(Config config, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = config.Host,
            Port = config.Port,
            Database = "postgres",
            Username = config.User,
            Password = config.Password,
        };
        using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\";", connection);
        command.ExecuteNonQuery();
    }
}
