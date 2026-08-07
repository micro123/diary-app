using Diary.App;
using Diary.Core.Configure;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.UtilTests;

[TestClass]
public sealed class TrackerPluginLifecycleCoordinatorTests
{
    [TestCleanup]
    public void Cleanup()
    {
        var path = Path.Combine(
            Diary.Utils.FsTools.GetApplicationConfigDirectory(),
            "tracker_lifecycle_tests.json");
        if (File.Exists(path))
            File.Delete(path);
    }

    [TestMethod]
    public void RegisterWithoutPlugins_LeavesCoreRegistriesEmpty()
    {
        var registry = new PluginInstanceRegistry();
        var uiRegistry = new TrackerUiContributionRegistry();
        var coordinator = CreateCoordinator(registry, uiRegistry);

        coordinator.Register(new object(), Array.Empty<ITrackerPlugin>(),
            new Dictionary<string, object>());

        Assert.AreEqual(0, registry.Instances.Count);
        Assert.AreEqual(0, uiRegistry.Contributions.Count);
    }

    [TestMethod]
    public void RegisterEnumeratesEnabledInstancesAndTheirUiContributions()
    {
        var registry = new PluginInstanceRegistry();
        var uiRegistry = new TrackerUiContributionRegistry();
        var plugin = new MemoryPlugin();
        var coordinator = CreateCoordinator(registry, uiRegistry);

        coordinator.Register(new object(), new[] { plugin },
            new Dictionary<string, object> { [plugin.Manifest.Id] = new object() });

        CollectionAssert.AreEquivalent(
            new[] { "memory.one", "memory.two" },
            registry.Instances.Select(instance => instance.InstanceId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "memory.one", "memory.two" },
            uiRegistry.Contributions.Select(contribution => contribution.Instance.InstanceId).ToArray());
    }

    [TestMethod]
    public void RegisterKeepsOtherInstancesWhenOneRegistrationFails()
    {
        var registry = new PluginInstanceRegistry();
        var uiRegistry = new TrackerUiContributionRegistry();
        var plugin = new MemoryPlugin(failingInstanceId: "memory.one");
        var coordinator = CreateCoordinator(registry, uiRegistry);

        coordinator.Register(new object(), new[] { plugin },
            new Dictionary<string, object> { [plugin.Manifest.Id] = new object() });

        Assert.AreEqual(1, registry.Instances.Count);
        Assert.AreEqual("memory.two", registry.Instances.Single().InstanceId);
        Assert.AreEqual(1, uiRegistry.Contributions.Count);
        Assert.AreEqual(
            TrackerInstanceState.MigrationFailed,
            registry.GetEntry(plugin.Manifest.Id, "memory.one")!.State);
    }

    [TestMethod]
    public void RegisterAfterDatabaseReloadRecreatesInstances()
    {
        var registry = new PluginInstanceRegistry();
        var uiRegistry = new TrackerUiContributionRegistry();
        var plugin = new MemoryPlugin();
        var coordinator = CreateCoordinator(registry, uiRegistry);
        var configuration = new MemoryConfiguration();

        coordinator.Register(new object(), new[] { plugin },
            new Dictionary<string, object> { [plugin.Manifest.Id] = configuration });
        var first = registry.Get(plugin.Manifest.Id, "memory.one");

        coordinator.Register(new object(), new[] { plugin },
            new Dictionary<string, object> { [plugin.Manifest.Id] = configuration });

        Assert.IsNotNull(first);
        Assert.AreNotSame(first, registry.Get(plugin.Manifest.Id, "memory.one"));
        Assert.AreEqual(2, uiRegistry.Contributions.Count);
    }

    [TestMethod]
    public void DiagnosticsSnapshotIdentifiesFailedInstanceAndRetryRestoresIt()
    {
        var registry = new PluginInstanceRegistry();
        var uiRegistry = new TrackerUiContributionRegistry();
        var plugin = new MemoryPlugin("memory.one", failOnce: true);
        var coordinator = CreateCoordinator(registry, uiRegistry);
        var diagnostics = new TrackerPluginDiagnosticsService(
            registry,
            coordinator,
            NullLogger<TrackerPluginDiagnosticsService>.Instance);
        diagnostics.SetPluginStates(new[]
        {
            new TrackerPluginLoadDiagnostic(
                plugin,
                new PluginLoadResult(PluginState.Compatible)),
        });

        coordinator.Register(new object(), new[] { plugin },
            new Dictionary<string, object> { [plugin.Manifest.Id] = new object() });

        var failed = diagnostics.GetSnapshot()
            .Single(entry => entry.InstanceId == "memory.one");
        Assert.AreEqual(TrackerInstanceState.MigrationFailed, failed.InstanceState);
        Assert.IsTrue(failed.CanRetry);
        Assert.IsTrue(diagnostics.Retry(plugin.Manifest.Id, "memory.one"));
        Assert.AreEqual(
            TrackerInstanceState.Enabled,
            registry.GetEntry(plugin.Manifest.Id, "memory.one")!.State);
        Assert.AreEqual(2, uiRegistry.Contributions.Count);
    }

    [TestMethod]
    public void UninstallInstanceDisablesAndPreservesDataByDefault()
    {
        var registry = new PluginInstanceRegistry();
        var uiRegistry = new TrackerUiContributionRegistry();
        var plugin = new MemoryPlugin();
        var configuration = new MemoryConfiguration();
        var coordinator = CreateCoordinator(registry, uiRegistry);

        coordinator.Register(new object(), new[] { plugin },
            new Dictionary<string, object> { [plugin.Manifest.Id] = configuration });

        Assert.IsTrue(coordinator.UninstallInstance(plugin.Manifest.Id, "memory.one"));
        Assert.IsFalse(configuration.Enabled);
        Assert.IsFalse(configuration.DataDeleted);
        Assert.IsNull(registry.Get(plugin.Manifest.Id, "memory.one"));
        Assert.AreEqual(1, registry.Instances.Count);
    }

    [TestMethod]
    public void UninstallInstanceDeletesDataOnlyWhenExplicitlyRequested()
    {
        var registry = new PluginInstanceRegistry();
        var uiRegistry = new TrackerUiContributionRegistry();
        var plugin = new MemoryPlugin();
        var configuration = new MemoryConfiguration();
        var coordinator = CreateCoordinator(registry, uiRegistry);

        coordinator.Register(new object(), new[] { plugin },
            new Dictionary<string, object> { [plugin.Manifest.Id] = configuration });

        Assert.IsTrue(coordinator.UninstallInstance(plugin.Manifest.Id, "memory.one", deleteData: true));
        Assert.IsTrue(configuration.DataDeleted);
    }

    private static TrackerPluginLifecycleCoordinator CreateCoordinator(
        PluginInstanceRegistry registry,
        TrackerUiContributionRegistry uiRegistry)
        => new(
            new TrackerInstanceCoordinator(registry, NullLogger<TrackerInstanceCoordinator>.Instance),
            uiRegistry,
            new[] { new MemoryUiFactory() },
            registry,
            NullLogger<TrackerPluginLifecycleCoordinator>.Instance);

    private sealed class MemoryPlugin(
        string? failingInstanceId = null,
        bool failOnce = false) : ITrackerPlugin
    {
        private bool _failurePending = failingInstanceId is not null;

        public PluginManifest Manifest { get; } = new()
        {
            Id = "tracker.memory.lifecycle",
            Version = "1.0.0",
            ApiVersion = 1,
            SupportsMultipleInstances = true,
        };

        public void RegisterServices(IServiceCollection services) { }
        public object CreateConfiguration() => new object();
        public IEnumerable<IPluginMigration> GetMigrations() => Array.Empty<IPluginMigration>();
        public bool TryDeleteInstanceData(PluginHostContext hostContext, string instanceId)
        {
            ((MemoryConfiguration)hostContext.Configuration).DataDeleted = true;
            return true;
        }
        public ITrackerInstance CreateInstance(string instanceId, object configuration)
            => new MemoryInstance(instanceId);

        public IEnumerable<PluginInstanceConfiguration> GetInstanceConfigurations(object configuration)
            => new[]
            {
                new PluginInstanceConfiguration("memory.one", configuration),
                new PluginInstanceConfiguration("memory.two", configuration),
                new PluginInstanceConfiguration("memory.disabled", new object(), Enabled: false),
            };

        public bool TrySetInstanceEnabled(object configuration, string instanceId, bool enabled)
        {
            ((MemoryConfiguration)configuration).Enabled = enabled;
            return true;
        }

        public IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext)
        {
            var context = (PluginHostContext)hostContext;
            foreach (var item in context.InstanceConfigurations.Where(item => item.Enabled))
            {
                if (item.InstanceId == failingInstanceId && _failurePending)
                {
                    if (failOnce)
                        _failurePending = false;
                    yield return new PluginInstanceRegistration(
                        item.InstanceId,
                        null,
                        TrackerInstanceState.MigrationFailed,
                        "测试迁移失败");
                }
                else
                {
                    yield return new PluginInstanceRegistration(
                        item.InstanceId,
                        item.Configuration,
                        TrackerInstanceState.Enabled);
                }
            }
        }
    }

    [StorageFile("tracker_lifecycle_tests.json")]
    private sealed class MemoryConfiguration
    {
        public bool Enabled { get; set; } = true;
        public bool DataDeleted { get; set; }
    }

    private sealed class MemoryInstance(string instanceId) : ITrackerInstance
    {
        public string PluginId => "tracker.memory.lifecycle";
        public string InstanceId => instanceId;
        public string DisplayName => instanceId;
        public string Icon => "memory";
        public bool IsConfigured => true;
        public IDictionary<int, object?>? LoadBindingsByDate(string date) => null;
    }

    private sealed class MemoryUiFactory : ITrackerUiContributionFactory
    {
        public string PluginId => "tracker.memory.lifecycle";
        public ITrackerUiContribution Create(ITrackerInstance instance) => new MemoryContribution(instance);
    }

    private sealed class MemoryContribution(ITrackerInstance instance) : ITrackerUiContribution
    {
        public string PluginId => instance.PluginId;
        public ITrackerInstance Instance => instance;
        public ViewModelBase? CreateSettingsPage(object configuration) => null;
        public ViewModelBase? CreateManagementPage(string instanceId) => null;
        public ITrackerEditorExtension? CreateEditorExtension(string instanceId) => null;
    }

}
