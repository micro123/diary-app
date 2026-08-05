using System.Data.Common;
using Diary.PluginBase;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.UtilTests;

[TestClass]
public class PluginMigrationRunnerTests
{
    [TestMethod]
    public void PluginHost_BlocksIncompatiblePluginBeforeRegistration()
    {
        var plugin = new TestPlugin(shouldThrow: false);
        var context = new PluginCompatibilityContext(1, 1, 0, new HashSet<string>());
        plugin.Manifest = plugin.Manifest with { ApiVersion = 2 };

        var result = PluginHost.Register(plugin, context, new ServiceCollection());

        Assert.AreEqual(PluginState.Blocked, result.State);
        Assert.IsFalse(plugin.Registered);
    }

    [TestMethod]
    public void PluginHostReturnsBlockedWhenRegistrationThrows()
    {
        var plugin = new TestPlugin(shouldThrow: true);
        var context = new PluginCompatibilityContext(1, 1, 0, new HashSet<string>());

        var result = PluginHost.Register(plugin, context, new ServiceCollection());

        Assert.AreEqual(PluginState.Blocked, result.State);
        StringAssert.Contains(result.Error, "register");
    }

    [TestMethod]
    public void PluginHostReturnsMigrationFailedWhenMigrationFails()
    {
        var plugin = new TestPlugin(shouldThrow: true);
        var result = PluginHost.Migrate(plugin, 1, new TestContext());

        Assert.AreEqual(PluginState.MigrationFailed, result.State);
    }

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
    public void ManifestValidator_BlocksWhenApiVersionTooLow()
    {
        var manifest = new PluginManifest
        {
            Id = "tracker.test",
            Version = "1.0.0",
            ApiVersion = 0, // 低于 context.MinApiVersion=1
        };
        var context = new PluginCompatibilityContext(1, 1, 0, new HashSet<string>());

        Assert.IsFalse(PluginCompatibilityValidator.Validate(manifest, context, out var error));
        StringAssert.Contains(error, "API 版本");
    }

    [TestMethod]
    public void ManifestValidator_BlocksWhenCoreDataVersionTooLow()
    {
        var manifest = new PluginManifest
        {
            Id = "tracker.test",
            Version = "1.0.0",
            ApiVersion = 1,
            MinCoreDataVersion = 2, // 高于 context.CoreDataVersion=0
        };
        var context = new PluginCompatibilityContext(1, 1, 0, new HashSet<string>());

        Assert.IsFalse(PluginCompatibilityValidator.Validate(manifest, context, out var error));
        StringAssert.Contains(error, "核心数据库版本");
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

    [TestMethod]
    public void Upgrade_ReturnsFalseWhenMigrationSourcesDuplicate()
    {
        var result = PluginMigrationRunner.Upgrade(
            "tracker.test", 1, 3,
            new IPluginMigration[]
            {
                new TestMigration(1, 2, new List<uint>()),
                new TestMigration(1, 3, new List<uint>()),
            },
            new TestContext());

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Upgrade_ReturnsFalseWhenMigrationThrows()
    {
        var result = PluginMigrationRunner.Upgrade(
            "tracker.test", 1, 2,
            new IPluginMigration[] { new TestMigration(1, 2, new List<uint>(), true) },
            new TestContext());

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void Upgrade_CapturesErrorMessageWhenMigrationThrows()
    {
        var result = PluginMigrationRunner.Upgrade(
            "tracker.test", 1, 2,
            new IPluginMigration[] { new TestMigration(1, 2, new List<uint>(), true) },
            new TestContext(),
            out var error);

        Assert.IsFalse(result);
        StringAssert.Contains(error, "migration failed");
    }

    [TestMethod]
    public void Upgrade_CapturesErrorWhenChainIsBroken()
    {
        var result = PluginMigrationRunner.Upgrade(
            "tracker.test", 1, 3,
            new IPluginMigration[] { new TestMigration(2, 3, new List<uint>()) },
            new TestContext(),
            out var error);

        Assert.IsFalse(result);
        Assert.IsFalse(string.IsNullOrEmpty(error));
        StringAssert.Contains(error, "1");
    }

    private sealed class TestMigration(uint from, uint to, List<uint> applied, bool shouldThrow = false) : IPluginMigration
    {
        public string PluginId => "tracker.test";
        public uint FromVersion { get; init; } = from;
        public uint ToVersion { get; init; } = to;

        public bool Up(IPluginMigrationContext context)
        {
            if (shouldThrow)
                throw new InvalidOperationException("migration failed");
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

    private sealed class TestPlugin(bool shouldThrow) : ITrackerPlugin
    {
        public PluginManifest Manifest { get; set; } = new()
        {
            Id = "tracker.test",
            Version = "1.0.0",
            ApiVersion = 1,
        };
        public bool Registered { get; private set; }

        public void RegisterServices(IServiceCollection services)
        {
            if (shouldThrow)
                throw new InvalidOperationException("register failed");
            Registered = true;
        }

        public object CreateConfiguration() => new();
        public IEnumerable<IPluginMigration> GetMigrations()
            => new[] { new TestMigration(1, 2, new List<uint>(), shouldThrow) };
        public ITrackerInstance CreateInstance(string instanceId, object configuration) => throw new NotSupportedException();
        public IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext)
            => Array.Empty<PluginInstanceRegistration>();
    }
}
