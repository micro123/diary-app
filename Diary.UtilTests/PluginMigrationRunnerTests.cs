using System.Data.Common;
using Diary.PluginBase;

namespace Diary.UtilTests;

[TestClass]
public class PluginMigrationRunnerTests
{
    [TestMethod]
    public void ManifestValidator_AcceptsSupportedPlugin()
    {
        var manifest = new PluginManifest
        {
            Id = "tracker.test",
            Version = "1.0.0",
            ApiVersion = 2,
            MinCoreDataVersion = 1,
            RequiredCapabilities = new[] { PluginCapabilities.ForeignKeys },
        };
        var context = new PluginCompatibilityContext(
            1, 2, 1, new HashSet<string> { PluginCapabilities.ForeignKeys });

        Assert.IsTrue(PluginCompatibilityValidator.Validate(manifest, context, out var error));
        Assert.IsNull(error);
    }

    [TestMethod]
    public void ManifestValidatorRejectsMissingCapability()
    {
        var manifest = new PluginManifest
        {
            Id = "tracker.test",
            Version = "1.0.0",
            ApiVersion = 1,
            RequiredCapabilities = new[] { PluginCapabilities.ReturningClause },
        };
        var context = new PluginCompatibilityContext(1, 1, 0, new HashSet<string>());

        Assert.IsFalse(PluginCompatibilityValidator.Validate(manifest, context, out var error));
        StringAssert.Contains(error, PluginCapabilities.ReturningClause);
    }

    [TestMethod]
    public void Upgrade_AppliesVersionChainInOrder()
    {
        var applied = new List<uint>();
        var migrations = new IPluginMigration[]
        {
            new TestMigration(2, 3, applied),
            new TestMigration(1, 2, applied),
        };

        var result = PluginMigrationRunner.Upgrade(
            "tracker.test", 1, 3, migrations, new TestContext());

        Assert.IsTrue(result);
        CollectionAssert.AreEqual(new uint[] { 1, 2 }, applied);
    }

    [TestMethod]
    public void Upgrade_ReturnsFalseWhenChainIsBroken()
    {
        var result = PluginMigrationRunner.Upgrade(
            "tracker.test", 1, 3,
            new IPluginMigration[] { new TestMigration(2, 3, new List<uint>()) },
            new TestContext());

        Assert.IsFalse(result);
    }

    private sealed class TestMigration(uint from, uint to, List<uint> applied) : IPluginMigration
    {
        public string PluginId => "tracker.test";
        public uint FromVersion { get; init; } = from;
        public uint ToVersion { get; init; } = to;

        public bool Up(IPluginMigrationContext context)
        {
            applied.Add(FromVersion);
            return true;
        }
    }

    private sealed class TestContext : IPluginMigrationContext
    {
        public string ProviderName => "test";
        public uint CoreDataVersion => 1;
        public bool ExecRaw(string sql) => true;
        public List<T> Query<T>(string sql, Func<DbDataReader, T> map, params object[] args) => new();
    }
}
