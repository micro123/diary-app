using Diary.PluginBase;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.UtilTests;

[TestClass]
public sealed class PluginInstanceRegistryTests
{
    [TestMethod]
    public void RegistryCreatesMultipleMemoryTrackerInstances()
    {
        var registry = new PluginInstanceRegistry();
        var plugin = new MemoryTrackerPlugin(supportsMultipleInstances: true);

        Assert.IsTrue(registry.Create(plugin, "memory.one", new object()).Success);
        Assert.IsTrue(registry.Create(plugin, "memory.two", new object()).Success);
        Assert.AreEqual(2, registry.Instances.Count);
        Assert.AreEqual("memory.two", registry.Get(plugin.Manifest.Id, "memory.two")!.InstanceId);
    }

    [TestMethod]
    public void RegistryRejectsDuplicateOrUnsupportedInstances()
    {
        var registry = new PluginInstanceRegistry();
        var single = new MemoryTrackerPlugin(supportsMultipleInstances: false);

        Assert.IsTrue(registry.Create(single, "memory.default", new object()).Success);
        var duplicate = registry.Create(single, "memory.default", new object());
        var second = registry.Create(single, "memory.other", new object());

        Assert.IsFalse(duplicate.Success);
        Assert.IsFalse(second.Success);
    }

    [TestMethod]
    public void RegistryDisablesInstanceWithoutRemovingConfigurationEntry()
    {
        var registry = new PluginInstanceRegistry();
        var plugin = new MemoryTrackerPlugin(supportsMultipleInstances: true);

        Assert.IsTrue(registry.Create(plugin, "memory.default", new object()).Success);
        Assert.IsTrue(registry.Disable(plugin.Manifest.Id, "memory.default"));
        Assert.AreEqual(0, registry.Instances.Count);
        Assert.AreEqual(TrackerInstanceState.Disabled,
            registry.GetEntry(plugin.Manifest.Id, "memory.default")!.State);
        Assert.IsTrue(registry.Clear(plugin.Manifest.Id, "memory.default"));
    }

    private sealed class MemoryTrackerPlugin(bool supportsMultipleInstances) : ITrackerPlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = "tracker.memory",
            Version = "1.0.0",
            ApiVersion = 1,
            SupportsMultipleInstances = supportsMultipleInstances,
        };

        public void RegisterServices(IServiceCollection services) { }
        public object CreateConfiguration() => new object();
        public IEnumerable<IPluginMigration> GetMigrations() => Array.Empty<IPluginMigration>();
        public IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext)
            => Array.Empty<PluginInstanceRegistration>();
        public ITrackerInstance CreateInstance(string instanceId, object configuration)
            => new MemoryTrackerInstance(instanceId);
    }

    private sealed class MemoryTrackerInstance(string instanceId) : ITrackerInstance
    {
        public string PluginId => "tracker.memory";
        public string InstanceId => instanceId;
        public string DisplayName => instanceId;
        public string Icon => "memory";
        public bool IsConfigured => true;
        public IDictionary<int, object?>? LoadBindingsByDate(string date) => new Dictionary<int, object?>();
    }
}
