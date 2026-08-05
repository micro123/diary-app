using Diary.App;
using Diary.Core.Configure;
using Diary.Core.Utils;
using Diary.PluginBase;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Diary.UtilTests;

[TestClass]
public sealed class PluginConfigurationLoaderTests
{
    private const string TestFileName = "plugin_configuration_loader_tests.json";

    [TestCleanup]
    public void CleanupConfigurationFile()
    {
        var path = Path.Combine(Diary.Utils.FsTools.GetApplicationConfigDirectory(), TestFileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    [TestMethod]
    public void LoaderCreatesConfigurationForPlugin()
    {
        var plugin = new MemoryPlugin();
        var loader = new PluginConfigurationLoader();

        var configuration = loader.Load(plugin);

        Assert.AreSame(plugin.Configuration, configuration);
        Assert.AreEqual(1, plugin.CreateConfigurationCalls);
    }

    [TestMethod]
    public void HostContextCarriesLoadedConfigurationAndDatabase()
    {
        var plugin = new MemoryPlugin();
        var loader = new PluginConfigurationLoader();
        var configuration = loader.Load(plugin);
        var database = new object();

        plugin.GetInstanceRegistrations(new PluginHostContext(database, configuration));

        var context = (PluginHostContext)plugin.LastContext!;
        Assert.AreSame(database, context.Database);
        Assert.AreSame(configuration, context.Configuration);
    }

    [TestMethod]
    public void LegacyConfiguration_IsMigratedAndUnknownFieldsAreRetained()
    {
        var seed = new TestConfiguration();
        var original = new JObject
        {
            ["Value"] = "legacy",
            ["Unknown"] = new JObject { ["Keep"] = true },
        };
        EasySaveLoad.SaveJson(seed, original);

        var configuration = (TestConfiguration)new PluginConfigurationLoader().Load(new MigratingPlugin());
        Assert.AreEqual("legacy-v1-v2", configuration.Value);
        Assert.AreEqual("added", configuration.Added);

        var saved = JObject.Parse(File.ReadAllText(
            Path.Combine(Diary.Utils.FsTools.GetApplicationConfigDirectory(), TestFileName)));
        Assert.AreEqual("tracker.test.migrating", (string?)saved["PluginId"]);
        Assert.AreEqual(2, (int?)saved["SchemaVersion"]);
        Assert.AreEqual(true, (bool?)saved["Payload"]!["Unknown"]!["Keep"]);
    }

    [TestMethod]
    public void FailedMigration_LeavesOriginalConfigurationUntouched()
    {
        var seed = new TestConfiguration();
        var original = new JObject
        {
            ["Value"] = "must-survive",
            ["Unknown"] = "untouched",
        };
        EasySaveLoad.SaveJson(seed, original);
        var path = Path.Combine(Diary.Utils.FsTools.GetApplicationConfigDirectory(), TestFileName);
        var originalText = File.ReadAllText(path);

        Assert.Throws<PluginConfigurationMigrationException>(
            () => new PluginConfigurationLoader().Load(new FailingMigrationPlugin()));

        Assert.AreEqual(originalText, File.ReadAllText(path));
    }

    [TestMethod]
    public void Save_PreservesPluginConfigurationPackage()
    {
        var seed = new TestConfiguration { Value = "saved" };
        EasySaveLoad.SaveJson(seed, new JObject
        {
            ["PluginId"] = "tracker.test.migrating",
            ["SchemaVersion"] = 2,
            ["Payload"] = new JObject
            {
                ["Value"] = "before",
                ["Unknown"] = new JObject { ["Keep"] = true },
            },
        });

        var plugin = new NonMigratingPlugin(seed);
        Assert.IsTrue(new PluginConfigurationLoader().Save(plugin, seed));

        var saved = JObject.Parse(File.ReadAllText(
            Path.Combine(Diary.Utils.FsTools.GetApplicationConfigDirectory(), TestFileName)));
        Assert.AreEqual("tracker.test.migrating", (string?)saved["PluginId"]);
        Assert.AreEqual(2, (int?)saved["SchemaVersion"]);
        Assert.AreEqual("saved", (string?)saved["Payload"]!["Value"]);
        Assert.AreEqual(true, (bool?)saved["Payload"]!["Unknown"]!["Keep"]);
    }

    private sealed class MemoryPlugin : ITrackerPlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = "tracker.memory",
            Version = "1.0.0",
            ApiVersion = 1,
        };

        public object Configuration { get; } = new();
        public int CreateConfigurationCalls { get; private set; }
        public object? LastContext { get; private set; }
        public void RegisterServices(IServiceCollection services) { }
        public object CreateConfiguration()
        {
            CreateConfigurationCalls++;
            return Configuration;
        }

        public IEnumerable<IPluginMigration> GetMigrations() => Array.Empty<IPluginMigration>();
        public ITrackerInstance CreateInstance(string instanceId, object configuration)
            => throw new NotSupportedException();
        public IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext)
        {
            LastContext = hostContext;
            return Array.Empty<PluginInstanceRegistration>();
        }
    }

    private sealed class NonMigratingPlugin(TestConfiguration configuration) : ITrackerPlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = "tracker.test.migrating",
            Version = "1.0.0",
            ApiVersion = 1,
        };

        public object CreateConfiguration() => configuration;
        public void RegisterServices(IServiceCollection services) { }
        public IEnumerable<IPluginMigration> GetMigrations() => Array.Empty<IPluginMigration>();
        public IEnumerable<IPluginConfigurationMigration> GetConfigurationMigrations()
            => Array.Empty<IPluginConfigurationMigration>();
        public ITrackerInstance CreateInstance(string instanceId, object configuration)
            => throw new NotSupportedException();
        public IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext)
            => Array.Empty<PluginInstanceRegistration>();
    }

    [StorageFile(TestFileName)]
    private sealed class TestConfiguration
    {
        public string Value { get; set; } = "default";
        public string Added { get; set; } = "";
    }

    private sealed class MigratingPlugin : TestMigrationPluginBase
    {
        public override IEnumerable<IPluginConfigurationMigration> GetConfigurationMigrations()
        {
            yield return new DelegateConfigurationMigration(0, 1, configuration =>
            {
                var json = (JObject)configuration;
                json["Value"] = $"{(string?)json["Value"]}-v1";
                return json;
            });
            yield return new DelegateConfigurationMigration(1, 2, configuration =>
            {
                var json = (JObject)configuration;
                json["Value"] = $"{(string?)json["Value"]}-v2";
                json["Added"] = "added";
                return json;
            });
        }
    }

    private sealed class FailingMigrationPlugin : TestMigrationPluginBase
    {
        public override IEnumerable<IPluginConfigurationMigration> GetConfigurationMigrations()
            => new[] { new DelegateConfigurationMigration(0, 1, _ => throw new InvalidOperationException("broken")) };
    }

    private abstract class TestMigrationPluginBase : ITrackerPlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = "tracker.test.migrating",
            Version = "1.0.0",
            ApiVersion = 1,
        };

        public object CreateConfiguration() => new TestConfiguration();
        public void RegisterServices(IServiceCollection services) { }
        public IEnumerable<IPluginMigration> GetMigrations() => Array.Empty<IPluginMigration>();
        public ITrackerInstance CreateInstance(string instanceId, object configuration)
            => throw new NotSupportedException();
        public IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext)
            => Array.Empty<PluginInstanceRegistration>();

        public abstract IEnumerable<IPluginConfigurationMigration> GetConfigurationMigrations();
    }

    private sealed class DelegateConfigurationMigration(
        int fromVersion,
        int toVersion,
        Func<object, object> migrate) : IPluginConfigurationMigration
    {
        public string PluginId => "tracker.test.migrating";
        public int FromVersion => fromVersion;
        public int ToVersion => toVersion;
        public object Migrate(object configuration) => migrate(configuration);
    }
}
