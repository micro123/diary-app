using System.Data.SQLite;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.MigrationTool;
using Diary.RedMine;

namespace Diary.DbTests;

[TestClass]
public sealed class MigrationToolTests
{
    [TestMethod]
    public void SQLite_ImportsCoreDataAsReadOnlyWithoutTrackerData()
    {
        using var db = TestDb.Create();
        var redMine = GetRedMine(db);
        redMine.AddRedMineActivity(99, "旧活动");
        redMine.AddRedMineProject(99, "旧项目", "旧描述");

        using var legacy = LegacyDatabase.Create(data: $"""
            INSERT INTO tags(tag_id, tag_name, tag_color, tag_level, tag_disabled)
            VALUES (1, '红色标签', 4278190335, 0, 0);
            INSERT INTO tags(tag_id, tag_name, tag_color, tag_level, tag_disabled)
            VALUES (3, '蓝色标签', 4294901760, 1, 1);
            INSERT INTO work_items(work_id, hour, comment, note, create_date, act_id, issue_id, is_uploaded, priority)
            VALUES (1, 2.5, '旧事项', NULL, '2026-08-10', NULL, NULL, 0, 4);
            INSERT INTO work_items(work_id, hour, comment, note, create_date, act_id, issue_id, is_uploaded, priority)
            VALUES (5, 0.75, '带备注事项', '旧备注', '2026-08-11', NULL, NULL, 0, 2);
            INSERT INTO work_item_tags(work_id, tag_id) VALUES (1, 1);
            INSERT INTO work_item_tags(work_id, tag_id) VALUES (5, 3);
            """);
        var progress = new List<(bool Success, double Value, string Message)>();

        var result = Migrator.MigrateFromSqlite(
            db,
            legacy.Path,
            (success, value, message) => progress.Add((success, value, message)));

        Assert.IsTrue(result);
        var item = db.GetWorkItemByDate("2026-08-10").Single();
        Assert.AreEqual(1, item.Id);
        Assert.AreEqual(2.5, item.Time, 0.0001);
        Assert.AreEqual("旧事项", item.Comment);
        Assert.AreEqual(WorkPriorities.P4, item.Priority);
        Assert.IsTrue(item.IsReadOnly);
        Assert.IsNull(redMine.WorkItemGetTimeEntry(item));

        var originalComment = item.Comment;
        var changedItem = item with { Comment = "不应保存" };
        Assert.IsFalse(db.UpdateWorkItem(changedItem));
        Assert.AreEqual(originalComment, db.GetWorkItemByDate("2026-08-10").Single().Comment);
        Assert.IsFalse(db.UpdateWorkItemId(item.Id, 20));
        Assert.ThrowsExactly<InvalidOperationException>(() => db.WorkUpdateNote(item, "不应保存"));
        Assert.ThrowsExactly<InvalidOperationException>(() => db.WorkDeleteNote(item));
        Assert.IsFalse(db.WorkItemRemoveTag(item, tag: new WorkTag { Id = 1 }));
        Assert.IsFalse(db.WorkItemCleanTags(item));
        Assert.IsNull(db.WorkGetNote(item));

        var tags = db.AllWorkTags();
        Assert.AreEqual(2, tags.Count);
        var tag = tags.Single(x => x.Id == 1);
        Assert.AreEqual(1, tag.Id);
        Assert.AreEqual("红色标签", tag.Name);
        Assert.AreEqual(0xFF0000, tag.Color);
        Assert.AreEqual(1, db.GetWorkItemTags(item).Single().Id);

        var remappedItem = db.GetWorkItemByDate("2026-08-11").Single();
        Assert.AreEqual(5, remappedItem.Id);
        Assert.AreEqual(0.75, remappedItem.Time, 0.0001);
        Assert.AreEqual(WorkPriorities.P2, remappedItem.Priority);
        Assert.IsTrue(remappedItem.IsReadOnly);
        Assert.AreEqual("旧备注", db.WorkGetNote(remappedItem));
        var remappedTag = tags.Single(x => x.Id == 3);
        Assert.AreEqual("蓝色标签", remappedTag.Name);
        Assert.AreEqual(0x0000FF, remappedTag.Color);
        Assert.IsTrue(remappedTag.Disabled);
        Assert.AreEqual(3, db.GetWorkItemTags(remappedItem).Single().Id);

        Assert.AreEqual(1, redMine.GetRedMineActivities().Count);
        Assert.AreEqual("旧活动", redMine.GetRedMineActivities().Single().Title);
        Assert.AreEqual(1, redMine.GetRedMineProjects().Count);
        Assert.AreEqual("旧项目", redMine.GetRedMineProjects().Single().Title);
        Assert.IsTrue(progress.Any(x => x.Success && x.Message.StartsWith("迁移完成")));
    }

    [TestMethod]
    public void SQLite_RejectsUnsupportedVersionAndLeavesTargetUnchanged()
    {
        using var db = TestDb.Create();
        var existing = db.CreateWorkItem("2026-08-09", "保留事项");
        existing.Time = 1.5;
        Assert.IsTrue(db.UpdateWorkItem(existing));

        using var legacy = LegacyDatabase.Create(version: 0x40000, includeTables: false);
        var progress = new List<(bool Success, double Value, string Message)>();

        var result = Migrator.MigrateFromSqlite(
            db,
            legacy.Path,
            (success, value, message) => progress.Add((success, value, message)));

        Assert.IsFalse(result);
        var retained = db.GetWorkItemByDate("2026-08-09").Single();
        Assert.AreEqual("保留事项", retained.Comment);
        Assert.AreEqual(1.5, retained.Time, 0.0001);
        Assert.IsTrue(db.BeginTransaction());
        Assert.IsTrue(db.RollbackTransaction());
        Assert.IsTrue(progress.Any(x => !x.Success && x.Message.Contains("版本错误")));
    }

    [TestMethod]
    public void SQLite_SkipsTagLinksWithMissingWorkItemsOrTags()
    {
        using var db = TestDb.Create();
        using var legacy = LegacyDatabase.Create(data: """
            INSERT INTO tags(tag_id, tag_name, tag_color, tag_level, tag_disabled)
            VALUES (1, '有效标签', 4278190335, 0, 0);
            INSERT INTO work_items(work_id, hour, comment, note, create_date, act_id, issue_id, is_uploaded, priority)
            VALUES (1, 1.0, '有效事项', NULL, '2026-08-24', NULL, NULL, 0, 1);
            INSERT INTO work_item_tags(work_id, tag_id) VALUES (1, 1);
            INSERT INTO work_item_tags(work_id, tag_id) VALUES (1, 99);
            INSERT INTO work_item_tags(work_id, tag_id) VALUES (99, 1);
            """);
        var progress = new List<(bool Success, double Value, string Message)>();

        var result = Migrator.MigrateFromSqlite(
            db,
            legacy.Path,
            (success, value, message) => progress.Add((success, value, message)));

        Assert.IsTrue(result);
        var item = db.GetWorkItemByDate("2026-08-24").Single();
        Assert.AreEqual(1, db.GetWorkItemTags(item).Single().Id);
        Assert.IsTrue(progress.Any(x => x.Success &&
            x.Message.Contains("跳过2条悬空标签关联") &&
            x.Message.Contains("缺失工作记录1条") &&
            x.Message.Contains("缺失标签1条")));
    }

    [TestMethod]
    public void SQLite_RollsBackCoreDataWhenImportFails()
    {
        using var db = TestDb.Create();
        var existing = db.CreateWorkItem("2026-08-09", "保留事项");
        existing.Time = 1.5;
        Assert.IsTrue(db.UpdateWorkItem(existing));
        var redMine = GetRedMine(db);
        redMine.AddRedMineActivity(99, "旧活动");
        redMine.AddRedMineProject(99, "旧项目", "旧描述");

        using var legacy = LegacyDatabase.Create(includeTables: false);
        var progress = new List<(bool Success, double Value, string Message)>();

        var result = Migrator.MigrateFromSqlite(
            db,
            legacy.Path,
            (success, value, message) => progress.Add((success, value, message)));

        Assert.IsFalse(result);
        var retained = db.GetWorkItemByDate("2026-08-09").Single();
        Assert.AreEqual("保留事项", retained.Comment);
        Assert.AreEqual(1.5, retained.Time, 0.0001);

        var reloadedRedMine = GetRedMine(db);
        Assert.AreEqual(1, reloadedRedMine.GetRedMineActivities().Count);
        Assert.AreEqual("旧活动", reloadedRedMine.GetRedMineActivities().Single().Title);
        Assert.AreEqual(1, reloadedRedMine.GetRedMineProjects().Count);
        Assert.IsTrue(db.BeginTransaction());
        Assert.IsTrue(db.RollbackTransaction());
        Assert.IsTrue(progress.Any(x => !x.Success));
    }

    private static IRedMineDb GetRedMine(DbInterfaceBase db)
        => db.GetExtension<IRedMineDb>(
            RedMinePluginConstants.DefaultInstanceId,
            new RedMinePlugin().GetMigrations())
           ?? throw new AssertFailedException("SQLite RedMine 扩展未加载");

    private sealed class LegacyDatabase : IDisposable
    {
        private LegacyDatabase(string path) => Path = path;

        public string Path { get; }

        public static LegacyDatabase Create(int version = 0x50000, string? data = null, bool includeTables = true)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"diarytoolpp-{Guid.NewGuid():N}.db.sqlite3");
            using (var connection = new SQLiteConnection($"Data Source={path};Version=3;"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = includeTables
                    ? """
                      CREATE TABLE redmine_activities(act_id INTEGER PRIMARY KEY, act_name VARCHAR(64) NOT NULL UNIQUE);
                      CREATE TABLE redmine_issues(issue_id INTEGER PRIMARY KEY, issue_name VARCHAR(128) NOT NULL UNIQUE, project_name VARCHAR(128) NOT NULL, assigned_to VARCHAR(32), is_closed INTEGER DEFAULT 0);
                      CREATE TABLE tags(tag_id INTEGER PRIMARY KEY AUTOINCREMENT, tag_name VARCHAR(32) NOT NULL UNIQUE, tag_color INTEGER NOT NULL, tag_level INTEGER DEFAULT 0, tag_disabled INTEGER DEFAULT 0);
                      CREATE TABLE work_items(work_id INTEGER PRIMARY KEY AUTOINCREMENT, hour REAL NOT NULL, comment VARCHAR(255) NOT NULL, note TEXT, create_date DATE NOT NULL, act_id INTEGER, issue_id INTEGER, is_uploaded INTEGER DEFAULT 0, priority INTEGER DEFAULT 0);
                      CREATE TABLE work_item_tags(work_id INTEGER NOT NULL, tag_id INTEGER NOT NULL, PRIMARY KEY(work_id, tag_id));
                      CREATE TABLE _version_(version_code INTEGER NOT NULL);
                      INSERT INTO _version_(version_code) VALUES ($version);
                      """
                    : """
                      CREATE TABLE _version_(version_code INTEGER NOT NULL);
                      INSERT INTO _version_(version_code) VALUES ($version);
                      """;
                command.Parameters.AddWithValue("$version", version);
                command.ExecuteNonQuery();

                if (!string.IsNullOrWhiteSpace(data))
                {
                    command.Parameters.Clear();
                    command.CommandText = data;
                    command.ExecuteNonQuery();
                }
            }

            return new LegacyDatabase(path);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                    File.Delete(Path);
            }
            catch (IOException)
            {
                // 测试结束时文件可能仍被 SQLite 原生句柄短暂占用。
            }
        }
    }
}
