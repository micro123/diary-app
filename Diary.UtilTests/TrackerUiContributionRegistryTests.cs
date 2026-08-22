using Diary.App;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;

namespace Diary.UtilTests;

[TestClass]
public sealed class TrackerUiContributionRegistryTests
{
    [TestMethod]
    public void RegistryCreatesOneContributionPerTrackerInstance()
    {
        var registry = new TrackerUiContributionRegistry();
        var instances = new ITrackerInstance[]
        {
            new MemoryInstance("one"),
            new MemoryInstance("two"),
        };

        registry.Register(new[] { new MemoryContributionFactory() }, instances);

        Assert.AreEqual(2, registry.Contributions.Count);
        CollectionAssert.AreEquivalent(
            new[] { "one", "two" },
            registry.Contributions.Select(x => x.Instance.InstanceId).ToArray());
    }

    [TestMethod]
    public void RegistryAllowsMissingPluginUiContribution()
    {
        var registry = new TrackerUiContributionRegistry();

        registry.Register(Array.Empty<ITrackerUiContributionFactory>(),
            new[] { new MemoryInstance("missing-ui") });

        Assert.AreEqual(0, registry.Contributions.Count);
    }

    [TestMethod]
    public void RegisterWithEmptyInstancesProducesNoContributions()
    {
        var registry = new TrackerUiContributionRegistry();

        // 无 tracker 实例时（如未安装/未配置任何 tracker），注册表应为空且不抛
        registry.Register(new[] { new MemoryContributionFactory() }, Array.Empty<ITrackerInstance>());

        Assert.AreEqual(0, registry.Contributions.Count);
    }

    [TestMethod]
    public void RegisterDisposesContributionsFromPreviousRegistration()
    {
        var factory = new DisposableContributionFactory();
        using var registry = new TrackerUiContributionRegistry();
        registry.Register([factory], [new MemoryInstance("one")]);

        registry.Register([factory], [new MemoryInstance("two")]);

        Assert.IsTrue(factory.Created.Single(item => item.Instance.InstanceId == "one").Disposed);
        Assert.IsFalse(factory.Created.Single(item => item.Instance.InstanceId == "two").Disposed);
    }

    private sealed class MemoryContributionFactory : ITrackerUiContributionFactory
    {
        public string PluginId => "tracker.memory";
        public ITrackerUiContribution Create(ITrackerInstance instance)
            => new MemoryContribution(instance);
    }

    private sealed class MemoryContribution(ITrackerInstance instance) : ITrackerUiContribution
    {
        public string PluginId => instance.PluginId;
        public ITrackerInstance Instance => instance;
        public ViewModelBase? CreateSettingsPage(object configuration) => null;
        public ViewModelBase? CreateManagementPage(string instanceId) => null;
        public ITrackerEditorExtension? CreateEditorExtension(string instanceId) => null;
    }

    private sealed class DisposableContributionFactory : ITrackerUiContributionFactory
    {
        public string PluginId => "tracker.memory";
        public List<DisposableContribution> Created { get; } = [];

        public ITrackerUiContribution Create(ITrackerInstance instance)
        {
            var contribution = new DisposableContribution(instance);
            Created.Add(contribution);
            return contribution;
        }
    }

    private sealed class DisposableContribution(ITrackerInstance instance)
        : ITrackerUiContribution, IDisposable
    {
        public bool Disposed { get; private set; }
        public string PluginId => instance.PluginId;
        public ITrackerInstance Instance => instance;
        public ViewModelBase? CreateSettingsPage(object configuration) => null;
        public ViewModelBase? CreateManagementPage(string instanceId) => null;
        public ITrackerEditorExtension? CreateEditorExtension(string instanceId) => null;
        public void Dispose() => Disposed = true;
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
