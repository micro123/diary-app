using Diary.App;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.UtilTests;

[TestClass]
public sealed class TrackerPluginLifecycleCoordinatorTests
{
    [TestMethod]
    public void RegisterWithoutPlugins_LeavesCoreRegistriesEmpty()
    {
        var registry = new PluginInstanceRegistry();
        var uiRegistry = new TrackerUiContributionRegistry();
        var templateRegistry = new TrackerTemplateContributorRegistry();
        var coordinator = CreateCoordinator(registry, uiRegistry, templateRegistry);

        coordinator.Register(new object(), Array.Empty<ITrackerPlugin>(),
            new Dictionary<string, object>());

        Assert.AreEqual(0, registry.Instances.Count);
        Assert.AreEqual(0, uiRegistry.Contributions.Count);
        Assert.AreEqual(0, templateRegistry.Contributors.Count);
    }

    [TestMethod]
    public void RegisterEnumeratesEnabledInstancesAndTheirUiContributions()
    {
        var registry = new PluginInstanceRegistry();
        var uiRegistry = new TrackerUiContributionRegistry();
        var templateRegistry = new TrackerTemplateContributorRegistry();
        var plugin = new MemoryPlugin();
        var coordinator = CreateCoordinator(registry, uiRegistry, templateRegistry);

        coordinator.Register(new object(), new[] { plugin },
            new Dictionary<string, object> { [plugin.Manifest.Id] = new object() });

        CollectionAssert.AreEquivalent(
            new[] { "memory.one", "memory.two" },
            registry.Instances.Select(instance => instance.InstanceId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "memory.one", "memory.two" },
            uiRegistry.Contributions.Select(contribution => contribution.Instance.InstanceId).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "memory.one", "memory.two" },
            templateRegistry.Contributors.Select(contributor => contributor.InstanceId).ToArray());
    }

    [TestMethod]
    public void RegisterKeepsOtherInstancesWhenOneRegistrationFails()
    {
        var registry = new PluginInstanceRegistry();
        var uiRegistry = new TrackerUiContributionRegistry();
        var templateRegistry = new TrackerTemplateContributorRegistry();
        var plugin = new MemoryPlugin(failingInstanceId: "memory.one");
        var coordinator = CreateCoordinator(registry, uiRegistry, templateRegistry);

        coordinator.Register(new object(), new[] { plugin },
            new Dictionary<string, object> { [plugin.Manifest.Id] = new object() });

        Assert.AreEqual(1, registry.Instances.Count);
        Assert.AreEqual("memory.two", registry.Instances.Single().InstanceId);
        Assert.AreEqual(1, uiRegistry.Contributions.Count);
        Assert.AreEqual(1, templateRegistry.Contributors.Count);
        Assert.AreEqual(
            TrackerInstanceState.MigrationFailed,
            registry.GetEntry(plugin.Manifest.Id, "memory.one")!.State);
    }

    [TestMethod]
    public void DiagnosticsSnapshotIdentifiesFailedInstanceAndRetryRestoresIt()
    {
        var registry = new PluginInstanceRegistry();
        var uiRegistry = new TrackerUiContributionRegistry();
        var templateRegistry = new TrackerTemplateContributorRegistry();
        var plugin = new MemoryPlugin("memory.one", failOnce: true);
        var coordinator = CreateCoordinator(registry, uiRegistry, templateRegistry);
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
        Assert.AreEqual(2, templateRegistry.Contributors.Count);
    }

    private static TrackerPluginLifecycleCoordinator CreateCoordinator(
        PluginInstanceRegistry registry,
        TrackerUiContributionRegistry uiRegistry,
        TrackerTemplateContributorRegistry templateRegistry)
        => new(
            new TrackerInstanceCoordinator(registry, NullLogger<TrackerInstanceCoordinator>.Instance),
            uiRegistry,
            templateRegistry,
            new[] { new MemoryUiFactory() },
            new[] { new MemoryTemplateFactory() },
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
        public ITrackerInstance CreateInstance(string instanceId, object configuration)
            => new MemoryInstance(instanceId);

        public IEnumerable<PluginInstanceConfiguration> GetInstanceConfigurations(object configuration)
            => new[]
            {
                new PluginInstanceConfiguration("memory.one", new object()),
                new PluginInstanceConfiguration("memory.two", new object()),
                new PluginInstanceConfiguration("memory.disabled", new object(), Enabled: false),
            };

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

    private sealed class MemoryTemplateFactory : ITrackerTemplateContributorFactory
    {
        public string PluginId => "tracker.memory.lifecycle";
        public ITrackerTemplateContributor Create(ITrackerInstance instance)
            => new MemoryContributor(instance.InstanceId);
    }

    private sealed class MemoryContributor(string instanceId) : ITrackerTemplateContributor
    {
        public string PluginId => "tracker.memory.lifecycle";
        public string InstanceId => instanceId;
        public int CurrentSchemaVersion => 1;
        public object CreateDefaultData() => new object();
        public ViewModelBase CreateEditor(object? data, TemplateEditorContext context) => new();
        public object ExtractData(ViewModelBase editor) => new object();
        public string Serialize(object data) => "{}";
        public object? Deserialize(string payloadJson, int schemaVersion) => new object();
        public void ApplyTo(object data, ITrackerEditorExtension target) { }
    }
}
