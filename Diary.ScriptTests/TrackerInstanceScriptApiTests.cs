using Diary.PluginBase;
using Diary.ScriptBase;
using Diary.ScriptHost;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.ScriptTests;

[TestClass]
public sealed class TrackerInstanceScriptApiTests
{
    [TestMethod]
    public void Get_UsesPluginIdAndInstanceId()
    {
        var registry = new PluginInstanceRegistry();
        var plugin = new MemoryPlugin();
        registry.Create(plugin, "company", new object());
        registry.Create(plugin, "personal", new object());
        var api = new TrackerInstanceScriptApi(registry);

        var result = api.Get("tracker.memory", "personal");

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("personal", result.Instance!.InstanceId);
        Assert.AreEqual("personal", result.Instance.DisplayName);
    }

    [TestMethod]
    public void Get_ReturnsUnavailableInstanceWithoutPermissionGate()
    {
        var registry = new PluginInstanceRegistry();

        var missing = new TrackerInstanceScriptApi(registry)
            .Get("tracker.memory", "default");

        Assert.AreEqual(TrackerScriptErrorCode.InstanceUnavailable, missing.ErrorCode);
    }

    private sealed class MemoryPlugin : ITrackerPlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = "tracker.memory",
            Version = "1.0.0",
            ApiVersion = 1,
            SupportsMultipleInstances = true,
        };

        public void RegisterServices(IServiceCollection services) { }
        public object CreateConfiguration() => new object();
        public IEnumerable<IPluginMigration> GetMigrations() => [];
        public IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext) => [];
        public ITrackerInstance CreateInstance(string instanceId, object configuration) => new MemoryInstance(instanceId);
    }

    private sealed class MemoryInstance(string instanceId) : ITrackerInstance
    {
        public string PluginId => "tracker.memory";
        public string InstanceId => instanceId;
        public string DisplayName => instanceId;
        public string Icon => "memory";
        public bool IsConfigured => true;
        public IDictionary<int, object?>? LoadBindingsByDate(string date) => null;
    }
}
