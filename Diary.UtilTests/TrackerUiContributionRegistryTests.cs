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
