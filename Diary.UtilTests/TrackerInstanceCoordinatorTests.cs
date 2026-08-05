using Diary.App;
using Diary.PluginBase;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.UtilTests;

[TestClass]
public sealed class TrackerInstanceCoordinatorTests
{
    [TestMethod]
    public void MigrationFailedRegistrationRecordedButNotEnabled()
    {
        var registry = new PluginInstanceRegistry();
        var plugin = new MemoryPlugin("tracker.memory");
        var coordinator = new TrackerInstanceCoordinator(registry, NullLogger<TrackerInstanceCoordinator>.Instance);

        var registrations = new[]
        {
            new PluginInstanceRegistration("memory.failing", null, TrackerInstanceState.MigrationFailed, "数据库迁移失败"),
            new PluginInstanceRegistration("memory.ok", new object(), TrackerInstanceState.Enabled),
        };

        coordinator.Register(plugin, registrations);

        // 失败实例：不在 Instances（下游不消费），但在 AllEntries（诊断可见）
        Assert.AreEqual(1, registry.Instances.Count);
        Assert.AreEqual("memory.ok", registry.Instances.Single().InstanceId);

        var failedEntry = registry.GetEntry(plugin.Manifest.Id, "memory.failing")!;
        Assert.IsNotNull(failedEntry);
        Assert.AreEqual(TrackerInstanceState.MigrationFailed, failedEntry.State);
        Assert.IsNull(failedEntry.Instance);
        Assert.AreEqual("数据库迁移失败", failedEntry.Error);

        var okEntry = registry.GetEntry(plugin.Manifest.Id, "memory.ok")!;
        Assert.AreEqual(TrackerInstanceState.Enabled, okEntry.State);
        Assert.IsNotNull(okEntry.Instance);
    }

    [TestMethod]
    public void CreateThrowingRecordsBlockedEntry()
    {
        var registry = new PluginInstanceRegistry();
        var plugin = new ThrowingCreatePlugin();

        var result = registry.Create(plugin, "memory.boom", new object());

        Assert.IsFalse(result.Success);
        Assert.AreEqual(TrackerInstanceState.Blocked, result.State);
        var entry = registry.GetEntry(plugin.Manifest.Id, "memory.boom")!;
        Assert.AreEqual(TrackerInstanceState.Blocked, entry.State);
        Assert.IsNull(entry.Instance);
        Assert.AreEqual(1, registry.AllEntries.Count);
        Assert.AreEqual(0, registry.Instances.Count);
    }

    [TestMethod]
    public void RecordSkipsExistingEntry()
    {
        var registry = new PluginInstanceRegistry();
        var plugin = new MemoryPlugin("tracker.memory");

        var coordinator = new TrackerInstanceCoordinator(registry, NullLogger<TrackerInstanceCoordinator>.Instance);
        coordinator.Register(plugin, new[]
        {
            new PluginInstanceRegistration("memory.x", null, TrackerInstanceState.MigrationFailed, "err"),
        });
        // 再次 Record 同一实例应被跳过，不覆盖、不抛
        registry.Record(plugin.Manifest.Id, "memory.x", TrackerInstanceState.Enabled, null);

        var entry = registry.GetEntry(plugin.Manifest.Id, "memory.x")!;
        Assert.AreEqual(TrackerInstanceState.MigrationFailed, entry.State);
    }

    private sealed class MemoryPlugin(string pluginId) : ITrackerPlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = pluginId,
            Version = "1.0.0",
            ApiVersion = 1,
            SupportsMultipleInstances = true,
        };

        public void RegisterServices(IServiceCollection services) { }
        public object CreateConfiguration() => new object();
        public IEnumerable<IPluginMigration> GetMigrations() => Array.Empty<IPluginMigration>();
        public ITrackerInstance CreateInstance(string instanceId, object configuration)
            => new MemoryInstance(pluginId, instanceId);
        public IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext)
            => Array.Empty<PluginInstanceRegistration>();
    }

    private sealed class ThrowingCreatePlugin : ITrackerPlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = "tracker.throwing",
            Version = "1.0.0",
            ApiVersion = 1,
            SupportsMultipleInstances = true,
        };

        public void RegisterServices(IServiceCollection services) { }
        public object CreateConfiguration() => new object();
        public IEnumerable<IPluginMigration> GetMigrations() => Array.Empty<IPluginMigration>();
        public ITrackerInstance CreateInstance(string instanceId, object configuration)
            => throw new InvalidOperationException("boom");
        public IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext)
            => Array.Empty<PluginInstanceRegistration>();
    }

    private sealed class MemoryInstance(string pluginId, string instanceId) : ITrackerInstance
    {
        public string PluginId => pluginId;
        public string InstanceId => instanceId;
        public string DisplayName => instanceId;
        public string Icon => "memory";
        public bool IsConfigured => true;
        public IDictionary<int, object?>? LoadBindingsByDate(string date) => new Dictionary<int, object?>();
    }
}
