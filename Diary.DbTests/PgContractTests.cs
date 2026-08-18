using Diary.Core;
using Diary.Database;

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
}
