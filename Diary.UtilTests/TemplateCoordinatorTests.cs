using Diary.App;
using Diary.App.Models;
using Diary.App.ViewModels;
using Diary.Core.Data.Base;
using Diary.Core.Data.App;
using Diary.Database;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.UtilTests;

[TestClass]
public sealed class TemplateCoordinatorTests
{
    [TestMethod]
    public void NewTemplate_CreatesDefaultContributorSlot()
    {
        var coordinator = CreateCoordinator(new MemoryContributor());
        var template = new Template { Name = "new" };

        var slots = coordinator.LoadEditors(template);
        var saved = coordinator.SaveEditors(slots, template);

        Assert.AreEqual(1, slots.Count);
        Assert.AreEqual(1, saved.Count);
        Assert.AreEqual("tracker.memory", saved[0].PluginId);
        Assert.AreEqual("memory.default", saved[0].InstanceId);
    }

    [TestMethod]
    public void NewTemplateWithoutTrackers_RemainsCoreOnly()
    {
        var coordinator = new TemplateCoordinator(new TrackerTemplateContributorRegistry());
        var template = new Template { Name = "core-only" };

        var slots = coordinator.LoadEditors(template);
        var saved = coordinator.SaveEditors(slots, template);

        Assert.AreEqual(0, slots.Count);
        Assert.AreEqual(0, saved.Count);
    }

    [TestMethod]
    public void WorkEditorWithoutTrackers_UsesCoreDefaults()
    {
        var editor = new WorkEditorViewModel(
            new DbShareData(NullLogger.Instance),
            new NoopPersistence(),
            new NoopUpload(),
            new TrackerUiContributionRegistry(),
            "核心工作项");

        Assert.AreEqual(0, editor.Extensions.Count);
        Assert.AreEqual("核心工作项", editor.Comment);
        Assert.IsTrue(editor.IsNewItem);
        Assert.IsTrue(editor.CanDelete());
        Assert.IsFalse(editor.CanUpload());
    }

    [TestMethod]
    public void InvalidPayload_IsPreserved()
    {
        var coordinator = CreateCoordinator(new MemoryContributor());
        var template = new Template
        {
            Name = "broken",
            Extensions = new[]
            {
                new TemplateExtensionData
                {
                    PluginId = "tracker.memory",
                    InstanceId = "memory.default",
                    SchemaVersion = 99,
                    PayloadJson = "not-json",
                },
            },
        };

        var slots = coordinator.LoadEditors(template);
        var saved = coordinator.SaveEditors(slots, template);

        Assert.AreEqual(0, slots.Count);
        Assert.AreEqual(1, saved.Count);
        Assert.AreEqual(99, saved[0].SchemaVersion);
        Assert.AreEqual("not-json", saved[0].PayloadJson);
    }

    [TestMethod]
    public void LegacyPayload_IsSavedAtCurrentSchemaVersion()
    {
        var coordinator = CreateCoordinator(new MemoryContributor());
        var template = new Template
        {
            Name = "legacy",
            Extensions = new[]
            {
                new TemplateExtensionData
                {
                    PluginId = "tracker.memory",
                    InstanceId = "memory.default",
                    SchemaVersion = 0,
                    PayloadJson = "legacy",
                },
            },
        };

        var slots = coordinator.LoadEditors(template);
        var saved = coordinator.SaveEditors(slots, template);

        Assert.AreEqual(1, slots.Count);
        Assert.AreEqual(1, saved[0].SchemaVersion);
        Assert.AreEqual("current", saved[0].PayloadJson);
    }

    [TestMethod]
    public void UnknownContributorPayload_IsPreserved()
    {
        var coordinator = CreateCoordinator(new MemoryContributor());
        var template = new Template
        {
            Name = "unknown",
            Extensions = new[]
            {
                new TemplateExtensionData
                {
                    PluginId = "tracker.other",
                    InstanceId = "other.default",
                    SchemaVersion = 4,
                    PayloadJson = "{\"value\":42}",
                },
            },
        };

        var saved = coordinator.SaveEditors(coordinator.LoadEditors(template), template);

        var unknown = saved.Single(x => x.PluginId == "tracker.other");
        Assert.AreEqual(2, saved.Count);
        Assert.AreEqual("{\"value\":42}", unknown.PayloadJson);
    }

    [TestMethod]
    public void Apply_DelegatesPayloadToMatchingExtension()
    {
        var coordinator = CreateCoordinator(new MemoryContributor());
        var template = new Template
        {
            Name = "apply",
            Extensions = new[]
            {
                new TemplateExtensionData
                {
                    PluginId = "tracker.memory",
                    InstanceId = "memory.default",
                    SchemaVersion = 1,
                    PayloadJson = "current",
                },
            },
        };
        var extension = new MemoryExtension();

        coordinator.Apply(template, new[] { extension });

        Assert.IsNotNull(extension.AppliedData);
    }

    private static TemplateCoordinator CreateCoordinator(ITrackerTemplateContributor contributor)
    {
        var registry = new TrackerTemplateContributorRegistry();
        registry.Register(
            new[] { new MemoryFactory(contributor) },
            new[] { new MemoryInstance() });
        return new TemplateCoordinator(registry);
    }

    private sealed class MemoryFactory(ITrackerTemplateContributor contributor)
        : ITrackerTemplateContributorFactory
    {
        public string PluginId => "tracker.memory";
        public ITrackerTemplateContributor Create(ITrackerInstance instance) => contributor;
    }

    private sealed class MemoryContributor : ITrackerTemplateContributor
    {
        public string PluginId => "tracker.memory";
        public string InstanceId => "memory.default";
        public int CurrentSchemaVersion => 1;
        public object CreateDefaultData() => new object();
        public ViewModelBase CreateEditor(object? data, TemplateEditorContext context) => new();
        public object ExtractData(ViewModelBase editor) => new object();
        public string Serialize(object data) => "current";
        public object? Deserialize(string payloadJson, int schemaVersion)
            => payloadJson == "not-json" || schemaVersion > CurrentSchemaVersion ? null : new object();
        public void ApplyTo(object data, ITrackerEditorExtension target) => target.ApplyTemplateData(data);
    }

    private sealed class MemoryExtension : ViewModelBase, ITrackerEditorExtension
    {
        public TrackerKey Key => new("tracker.memory", "memory.default");
        public string InstanceId => "memory.default";
        ViewModelBase ITrackerEditorExtension.View => this;
        public object? AppliedData { get; private set; }
        public bool IsLocked => false;
        public bool CanDelete => true;
        public void Load(Diary.Core.Data.Base.WorkItem? item, object? binding = null) { }
        public bool Save(Diary.Core.Data.Base.WorkItem item) => true;
        public void CloneTo(ITrackerEditorExtension? target) { }
        public Task<TrackerOperationResult> UploadAsync(Diary.Core.Data.Base.WorkItem item)
            => Task.FromResult(new TrackerOperationResult(true, null));
        public void ApplyTemplateData(object data) => AppliedData = data;
    }

    private sealed class MemoryInstance : ITrackerInstance
    {
        public string PluginId => "tracker.memory";
        public string InstanceId => "memory.default";
        public string DisplayName => "Memory";
        public string Icon => "memory";
        public bool IsConfigured => true;
        public IDictionary<int, object?>? LoadBindingsByDate(string date) => null;
    }

    private sealed class NoopPersistence : IWorkItemPersistenceCoordinator
    {
        public WorkItemSaveResult Save(DbInterfaceBase db, WorkItemSaveRequest request)
            => new(false, false, Error: "not used");
    }

    private sealed class NoopUpload : ITrackerUploadCoordinator
    {
        public Task<WorkUploadResult> UploadAsync(
            WorkItem item,
            IReadOnlyCollection<ITrackerEditorExtension> extensions)
            => Task.FromResult(new WorkUploadResult(Array.Empty<TrackerUploadResult>()));
    }
}
