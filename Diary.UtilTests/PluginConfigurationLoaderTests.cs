using Diary.App;
using Diary.PluginBase;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.UtilTests;

[TestClass]
public sealed class PluginConfigurationLoaderTests
{
    [TestMethod]
    public void LoaderCreatesConfigurationForPlugin()
    {
        var plugin = new MemoryPlugin();
        var loader = new PluginConfigurationLoader();

        var configuration = loader.Load(plugin);

        Assert.AreSame(plugin.Configuration, configuration);
        Assert.AreEqual(1, plugin.CreateConfigurationCalls);
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
            => Array.Empty<PluginInstanceRegistration>();
    }
}
