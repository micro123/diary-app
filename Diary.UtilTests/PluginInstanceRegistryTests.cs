using Diary.PluginBase;

namespace Diary.UtilTests;

[TestClass]
public class PluginInstanceRegistryTests
{
    [TestMethod]
    public void Create_RegistersMultipleInstances()
    {
        var registry = new PluginInstanceRegistry();
        var plugin = new TestPlugin();

        var first = registry.Create(plugin, "company", new object());
        var second = registry.Create(plugin, "personal", new object());

        Assert.IsTrue(first.Success);
        Assert.IsTrue(second.Success);
        Assert.AreEqual(2, registry.Instances.Count);
        Assert.AreSame(first.Instance, registry.Get("tracker.test", "company"));
    }

    [TestMethod]
    public void Create_RejectsDuplicateAndKeepsExistingInstance()
    {
        var registry = new PluginInstanceRegistry();
        var plugin = new TestPlugin();

        var first = registry.Create(plugin, "company", new object());
        var duplicate = registry.Create(plugin, "company", new object());

        Assert.IsTrue(first.Success);
        Assert.IsFalse(duplicate.Success);
        Assert.AreEqual(1, registry.Instances.Count);
    }

    private sealed class TestPlugin : ITrackerPlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = "tracker.test",
            Version = "1.0.0",
        };

        public void RegisterServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
        public object CreateConfiguration() => new();
        public IEnumerable<IPluginMigration> GetMigrations() => Array.Empty<IPluginMigration>();
        public ITrackerInstance CreateInstance(string instanceId, object configuration)
            => new TestInstance(instanceId);
    }

    private sealed class TestInstance(string instanceId) : ITrackerInstance
    {
        public string PluginId => "tracker.test";
        public string InstanceId => instanceId;
        public string DisplayName => instanceId;
        public string Icon => string.Empty;
        public bool IsConfigured => true;
        public IDictionary<int, object?>? LoadBindingsByDate(string date) => null;
    }
}
