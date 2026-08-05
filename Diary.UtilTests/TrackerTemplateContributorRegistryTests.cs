using Diary.App;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;

namespace Diary.UtilTests;

[TestClass]
public sealed class TrackerTemplateContributorRegistryTests
{
    [TestMethod]
    public void RegistryCreatesTemplateContributorPerInstance()
    {
        var registry = new TrackerTemplateContributorRegistry();
        var instances = new ITrackerInstance[]
        {
            new MemoryInstance("one"),
            new MemoryInstance("two"),
        };

        registry.Register(new[] { new MemoryFactory() }, instances);

        Assert.AreEqual(2, registry.Contributors.Count);
        CollectionAssert.AreEquivalent(
            new[] { "one", "two" },
            registry.Contributors.Select(x => x.InstanceId).ToArray());
    }

    [TestMethod]
    public void RegisterWithEmptyInstancesProducesNoContributors()
    {
        var registry = new TrackerTemplateContributorRegistry();

        registry.Register(new[] { new MemoryFactory() }, Array.Empty<ITrackerInstance>());

        Assert.AreEqual(0, registry.Contributors.Count);
    }

    private sealed class MemoryFactory : ITrackerTemplateContributorFactory
    {
        public string PluginId => "tracker.memory";
        public ITrackerTemplateContributor Create(ITrackerInstance instance)
            => new MemoryContributor(instance.InstanceId);
    }

    private sealed class MemoryContributor(string instanceId) : ITrackerTemplateContributor
    {
        public string PluginId => "tracker.memory";
        public string InstanceId => instanceId;
        public int CurrentSchemaVersion => 1;
        public object CreateDefaultData() => new object();
        public ViewModelBase CreateEditor(object? data, TemplateEditorContext context) => new();
        public object ExtractData(ViewModelBase editor) => new object();
        public string Serialize(object data) => "{}";
        public object? Deserialize(string payloadJson, int schemaVersion) => new object();
        public void ApplyTo(object data, ITrackerEditorExtension target) { }
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
