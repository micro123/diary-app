#if DEBUG
using Diary.App;
using Diary.Database;
using Diary.Db.SQLite;
using PostgreSqlConfig = Diary.Db.PostgreSQL.Config;

namespace Diary.AppTests;

[TestClass]
public sealed class DebugUiAutomationTests
{
    [TestMethod]
    [DataRow("1024", 1024)]
    [DataRow("9222", 9222)]
    [DataRow("65535", 65535)]
    public void TryParsePort_AcceptsValidPorts(string value, int expected)
    {
        var result = DebugUiAutomation.TryParsePort(value, out var port);

        Assert.IsTrue(result);
        Assert.AreEqual(expected, port);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("1023")]
    [DataRow("65536")]
    [DataRow("not-a-port")]
    public void TryParsePort_RejectsInvalidPorts(string? value)
    {
        Assert.IsFalse(DebugUiAutomation.TryParsePort(value, out _));
    }

    [TestMethod]
    [DataRow(null, "default")]
    [DataRow("", "default")]
    [DataRow(" EXTENDED ", "extended")]
    [DataRow("survey", "survey")]
    [DataRow("database-error", "database-error")]
    [DataRow("extra-fields", "extra-fields")]
    [DataRow("date-performance", "date-performance")]
    [DataRow("plugins", "plugins")]
    public void NormalizeScenario_AcceptsSupportedValues(string? value, string expected)
    {
        Assert.AreEqual(expected, DebugUiAutomation.NormalizeScenario(value));
    }

    [TestMethod]
    public void NormalizeScenario_RejectsUnknownValue()
    {
        Assert.ThrowsExactly<ArgumentException>(() => DebugUiAutomation.NormalizeScenario("unknown"));
    }

    [TestMethod]
    public void CreateIsolatedAppId_IsStableAndSeparatesProfiles()
    {
        var first = DebugUiAutomation.CreateIsolatedAppId("Diary.App", @"C:\ui-test\profile-a");
        var repeated = DebugUiAutomation.CreateIsolatedAppId("Diary.App", @"C:\ui-test\profile-a");
        var second = DebugUiAutomation.CreateIsolatedAppId("Diary.App", @"C:\ui-test\profile-b");

        Assert.AreEqual(first, repeated);
        Assert.AreNotEqual(first, second);
        StringAssert.StartsWith(first, "Diary.App.UiTest.");
        Assert.AreEqual("Diary.App.UiTest.".Length + 12, first.Length);
    }

    [TestMethod]
    public void ApplyDatePerformanceScenario_CreatesIdempotentRichDataset()
    {
        using var database = new SQLiteDb(new TestSqliteFactory());
        Assert.IsTrue(database.Connect());
        Assert.IsTrue(database.Initialized());
        var anchor = new DateTime(2026, 8, 24);

        Assert.IsTrue(DebugUiAutomation.ApplyDatePerformanceScenario(database, anchor));
        Assert.IsFalse(DebugUiAutomation.ApplyDatePerformanceScenario(database, anchor));

        var items = database.GetWorkItemByDate("2026-08-24");
        Assert.AreEqual(DebugUiAutomation.DatePerformanceItemsPerDay, items.Count);
        Assert.IsTrue(items.Any(item => item.Comment ==
            $"{DebugUiAutomation.DatePerformanceTitlePrefix} 2026-08-24 #00"));
        var itemWithTwoTags = items.First(candidate => candidate.Id % 2 == 0 && candidate.Id % 5 != 0);
        Assert.AreEqual(2, database.GetWorkItemTags(itemWithTwoTags).Count);
        Assert.IsTrue(items.Any(candidate => database.GetWorkItemTags(candidate).Count == 0));
        Assert.IsFalse(string.IsNullOrWhiteSpace(database.WorkGetNote(
            items.First(candidate => candidate.Id % 4 == 0))));
        Assert.AreEqual(1, database.GetWorkItemExtraFields(
            items.First(candidate => candidate.Id % 3 == 0)).Count);
        Assert.IsTrue(items.Any(candidate => database.GetWorkItemExtraFields(candidate).Count == 0));

        var host = (IDbExtensionHost)database;
        var total = Convert.ToInt32(host.ExecuteScalar(
            "SELECT COUNT(*) FROM work_items WHERE comment LIKE $title_like;",
            ("$title_like", DebugUiAutomation.DatePerformanceTitlePrefix + " %")));
        Assert.AreEqual(
            DebugUiAutomation.DatePerformanceDayCount * DebugUiAutomation.DatePerformanceItemsPerDay,
            total);
        Assert.AreEqual(total / 4, Convert.ToInt32(host.ExecuteScalar(
            "SELECT COUNT(*) FROM work_notes n INNER JOIN work_items w ON w.id=n.id WHERE w.comment LIKE $title_like;",
            ("$title_like", DebugUiAutomation.DatePerformanceTitlePrefix + " %"))));
        Assert.AreEqual(total * 13 / 10, Convert.ToInt32(host.ExecuteScalar(
            "SELECT COUNT(*) FROM work_item_tags t INNER JOIN work_items w ON w.id=t.work_id WHERE w.comment LIKE $title_like;",
            ("$title_like", DebugUiAutomation.DatePerformanceTitlePrefix + " %"))));
        Assert.AreEqual(total / 3, Convert.ToInt32(host.ExecuteScalar(
            "SELECT COUNT(*) FROM work_item_extra_field_values e INNER JOIN work_items w ON w.id=e.work_id WHERE w.comment LIKE $title_like;",
            ("$title_like", DebugUiAutomation.DatePerformanceTitlePrefix + " %"))));

        var sparseItems = DebugUiAutomation.BuildSparseWorkItemSelection("$title_like");
        Assert.AreEqual(total / 5, Convert.ToInt32(host.ExecuteScalar(
            $"SELECT COUNT(*) FROM ({sparseItems}) selected_items;",
            ("$title_like", DebugUiAutomation.DatePerformanceTitlePrefix + " %"))));
        var distributionQuery = $"SELECT {{0}} FROM ("
            + $"SELECT w.create_date, COUNT(*) AS binding_count FROM work_items w "
            + $"INNER JOIN ({sparseItems}) selected_items ON selected_items.id=w.id "
            + "GROUP BY w.create_date) daily_bindings;";
        Assert.AreEqual(9, Convert.ToInt32(host.ExecuteScalar(
            string.Format(distributionQuery, "MIN(binding_count)"),
            ("$title_like", DebugUiAutomation.DatePerformanceTitlePrefix + " %"))));
        Assert.AreEqual(10, Convert.ToInt32(host.ExecuteScalar(
            string.Format(distributionQuery, "MAX(binding_count)"),
            ("$title_like", DebugUiAutomation.DatePerformanceTitlePrefix + " %"))));
        Assert.AreEqual(DebugUiAutomation.DatePerformanceDayCount, Convert.ToInt32(host.ExecuteScalar(
            string.Format(distributionQuery, "COUNT(*)"),
            ("$title_like", DebugUiAutomation.DatePerformanceTitlePrefix + " %"))));
    }

    [TestMethod]
    public void ApplyPostgreSqlDatePerformanceConfiguration_UsesEnvironmentWithoutPersistingSecrets()
    {
        var config = new PostgreSqlConfig();
        var environment = new Dictionary<string, string?>
        {
            [DebugUiAutomation.PostgreSqlHostEnvironmentVariable] = "pg.test.local",
            [DebugUiAutomation.PostgreSqlPortEnvironmentVariable] = "5544",
            [DebugUiAutomation.PostgreSqlDatabaseEnvironmentVariable] = "diary_cdp_test",
            [DebugUiAutomation.PostgreSqlUserEnvironmentVariable] = "test_user",
            [DebugUiAutomation.PostgreSqlPasswordEnvironmentVariable] = "test-password",
        };

        var changed = DebugUiAutomation.ApplyPostgreSqlDatePerformanceConfiguration(
            "date-performance",
            "PostgreSQL",
            config,
            name => environment.GetValueOrDefault(name));

        Assert.IsTrue(changed);
        Assert.AreEqual("pg.test.local", config.Host);
        Assert.AreEqual((ushort)5544, config.Port);
        Assert.AreEqual("diary_cdp_test", config.Database);
        Assert.AreEqual("test_user", config.User);
        Assert.AreEqual("test-password", config.Password);
    }

    private sealed class TestSqliteFactory : IDbFactory
    {
        private readonly Config _config = new() { FilePath = ":memory:" };

        public string Name => "SQLite";
        public bool Usable => true;
        public DbInterfaceBase Create() => new SQLiteDb(this);
        public Migration? GetMigration(uint version) => null;
        public object GetConfig() => _config;
    }
}
#endif
