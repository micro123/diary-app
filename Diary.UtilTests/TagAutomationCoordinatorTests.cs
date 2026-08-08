using Diary.App;
using Diary.App.Models;
using Diary.App.ViewModels;
using Diary.Core.Data.Base;
using Diary.PluginBase;
using Diary.PluginUI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.UtilTests;

[TestClass]
public sealed class TagAutomationCoordinatorTests
{
    [TestMethod]
    public void AddTags_NotifiesOnlyNewTagsInInputOrder()
    {
        var automation = new RecordingAutomation();
        var editor = CreateEditor(automation);
        var first = new WorkTag { Id = 1, Name = "first" };
        var second = new WorkTag { Id = 2, Name = "second" };
        editor.WorkTags.Add(first);

        editor.AddTags(new[] { first, second, first }, TagAddSource.Batch);

        CollectionAssert.AreEqual(new[] { 2 }, automation.Tags.ToArray());
        CollectionAssert.AreEqual(new[] { 0 }, automation.Sequences.ToArray());
        Assert.AreEqual(TagAddSource.Batch, automation.Sources.Single());
        CollectionAssert.AreEqual(new[] { 1, 2 }, editor.WorkTags.Select(tag => tag.Id).ToArray());
    }

    [TestMethod]
    public void WorkEditor_ReportsAvailableTagsAfterTagDataIsLoaded()
    {
        var shareData = new DbShareData(NullLogger.Instance);
        var editor = new WorkEditorViewModel(
            shareData,
            new NoopPersistence(),
            new NoopUpload(),
            new TrackerUiContributionRegistry(),
            "test",
            new RecordingAutomation());

        Assert.IsFalse(editor.HasAvailableTags);
        shareData.WorkTags.Add(new WorkTag { Id = 1, Level = TagLevels.Primary });
        editor.SyncTags();

        Assert.IsTrue(editor.HasAvailableTags);
    }

    [TestMethod]
    public void WorkEditor_AllowsUploadingUnsavedWorkWhenTrackerIsOpen()
    {
        var editor = CreateEditor(new RecordingAutomation());

        Assert.IsFalse(editor.CanUpload());
        editor.Extensions.Add(new PlainExtension());

        Assert.IsTrue(editor.CanUpload());
    }

    [TestMethod]
    public void WorkEditor_RefreshesTrackerTabHeaderAfterRegistryUpdate()
    {
        var registry = new TrackerUiContributionRegistry();
        registry.Register([new MemoryContributionFactory()], [new MemoryInstance("default", "旧名称")]);
        var editor = new WorkEditorViewModel(
            new DbShareData(NullLogger.Instance),
            new NoopPersistence(),
            new NoopUpload(),
            registry,
            "test",
            new RecordingAutomation());

        registry.Register([new MemoryContributionFactory()], [new MemoryInstance("default", "新名称")]);
        editor.RefreshTrackerTabHeaders();

        Assert.AreEqual("新名称", editor.TrackerTabs.Single().Header);
    }

    [TestMethod]
    public void AddTags_TemplateSourcePreservesAddedTagOrder()
    {
        var automation = new RecordingAutomation();
        var editor = CreateEditor(automation);
        var tags = new[]
        {
            new WorkTag { Id = 3, Name = "development" },
            new WorkTag { Id = 4, Name = "overtime" },
            new WorkTag { Id = 5, Name = "urgent" },
        };

        editor.AddTags(tags, TagAddSource.Template);

        CollectionAssert.AreEqual(new[] { 3, 4, 5 }, automation.Tags.ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, automation.Sequences.ToArray());
        Assert.IsTrue(automation.Sources.All(source => source == TagAddSource.Template));
    }

    [TestMethod]
    public void Clone_ReappliesTagAutomationForCopiedTags()
    {
        var automation = new RecordingAutomation();
        var editor = CreateEditor(automation);
        editor.AddTags([
            new WorkTag { Id = 6, Name = "development" },
            new WorkTag { Id = 7, Name = "urgent" },
        ], TagAddSource.User);
        automation.Tags.Clear();
        automation.Sequences.Clear();
        automation.Sources.Clear();

        var clone = editor.Clone();

        CollectionAssert.AreEqual(new[] { 6, 7 }, clone.WorkTags.Select(tag => tag.Id).ToArray());
        CollectionAssert.AreEqual(new[] { 6, 7 }, automation.Tags.ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1 }, automation.Sequences.ToArray());
        Assert.IsTrue(automation.Sources.All(source => source == TagAddSource.Duplicate));
    }

    [TestMethod]
    public void Coordinator_AppliesDefaultsOnlyToCapableExtensions()
    {
        var capable = new TagDefaultsExtension();
        var incapable = new PlainExtension();

        var result = new TagAutomationCoordinator().TagAdded(
            null,
            new WorkTag { Id = 7 },
            new TagAutomationContext(TagAddSource.User, 0),
            new ITrackerEditorExtension[] { capable, incapable });

        Assert.AreEqual(7, capable.AppliedTagId);
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void Coordinator_IsolatesInstanceFailuresAndReturnsResults()
    {
        var failing = new TagDefaultsExtension("failing", true);
        var succeeding = new TagDefaultsExtension("succeeding");

        var result = new TagAutomationCoordinator().TagAdded(
            null,
            new WorkTag { Id = 9 },
            new TagAutomationContext(TagAddSource.User, 0),
            new ITrackerEditorExtension[] { failing, succeeding });

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(9, succeeding.AppliedTagId);
        Assert.AreEqual("failed", result.Instances.Single(item => item.TrackerKey.InstanceId == "failing").Error);
        Assert.IsTrue(result.Instances.Single(item => item.TrackerKey.InstanceId == "succeeding").Succeeded);
    }

    [TestMethod]
    public void Coordinator_AppliesSameTagToEachEnabledInstanceIndependently()
    {
        var company = new TagDefaultsExtension("company");
        var personal = new TagDefaultsExtension("personal");

        var result = new TagAutomationCoordinator().TagAdded(
            null,
            new WorkTag { Id = 15 },
            new TagAutomationContext(TagAddSource.Template, 1),
            [company, personal]);

        Assert.AreEqual(15, company.AppliedTagId);
        Assert.AreEqual(15, personal.AppliedTagId);
        CollectionAssert.AreEquivalent(
            new[] { "company", "personal" },
            result.Instances.Select(item => item.TrackerKey.InstanceId).ToArray());
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public void AddTags_PreservesLatestAutomationDiagnostics()
    {
        var expected = new TagAutomationResult([
            new TagAutomationInstanceResult(
                new TrackerKey("test", "default"),
                true,
                ["ActivityIndex"],
                [new TrackerTagDefaultConflict("ActivityId", ["first", "second"])],
                [new TrackerTagDefaultInvalidTarget("IssueId", "99", "invalid")])
        ]);
        var automation = new RecordingAutomation(expected);
        var editor = CreateEditor(automation);

        editor.AddTags([new WorkTag { Id = 12 }], TagAddSource.User);

        Assert.AreSame(expected, editor.LastTagAutomationResult);
        CollectionAssert.AreEqual(
            new[] { "ActivityIndex" },
            editor.LastTagAutomationResult!.Instances.Single().ChangedFields.ToArray());
    }

    private static WorkEditorViewModel CreateEditor(ITagAutomationCoordinator automation)
        => new(
            new DbShareData(NullLogger.Instance),
            new NoopPersistence(),
            new NoopUpload(),
            new TrackerUiContributionRegistry(),
            "test",
            automation);

    private sealed class RecordingAutomation(TagAutomationResult? result = null) : ITagAutomationCoordinator
    {
        public List<int> Tags { get; } = new();
        public List<int> Sequences { get; } = new();
        public List<TagAddSource> Sources { get; } = new();

        public TagAutomationResult TagAdded(
            WorkItem? item,
            WorkTag tag,
            TagAutomationContext context,
            IReadOnlyCollection<ITrackerEditorExtension> extensions)
        {
            Tags.Add(tag.Id);
            Sequences.Add(context.Sequence);
            Sources.Add(context.Source);
            return result ?? new TagAutomationResult(Array.Empty<TagAutomationInstanceResult>());
        }
    }

    private class PlainExtension : ITrackerEditorExtension
    {
        public TrackerKey Key => new("test", InstanceId);
        public virtual string InstanceId => "default";
        public Diary.GUIBase.ViewModels.ViewModelBase View { get; } = new();
        public bool IsLocked => false;
        public bool CanDelete => true;
        public void Load(WorkItem? item, object? binding = null) { }
        public bool Save(WorkItem item) => true;
        public void CloneTo(ITrackerEditorExtension? target) { }
        public Task<TrackerOperationResult> UploadAsync(WorkItem item)
            => Task.FromResult(new TrackerOperationResult(false));
    }

    private sealed class TagDefaultsExtension(
        string instanceId = "default",
        bool throws = false) : PlainExtension, ITrackerTagDefaults
    {
        public override string InstanceId => instanceId;
        public int AppliedTagId { get; private set; }

        public TrackerTagDefaultsResult ApplyTagDefaults(WorkTag tag)
        {
            if (throws)
                throw new InvalidOperationException("failed");
            AppliedTagId = tag.Id;
            return TrackerTagDefaultsResult.Empty;
        }
    }

    private sealed class MemoryContributionFactory : ITrackerUiContributionFactory
    {
        public string PluginId => "test";
        public ITrackerUiContribution Create(ITrackerInstance instance) => new MemoryContribution(instance);
    }

    private sealed class MemoryContribution(ITrackerInstance instance) : ITrackerUiContribution
    {
        public string PluginId => instance.PluginId;
        public ITrackerInstance Instance => instance;
        public Diary.GUIBase.ViewModels.ViewModelBase? CreateSettingsPage(object configuration) => null;
        public Diary.GUIBase.ViewModels.ViewModelBase? CreateManagementPage(string instanceId) => null;
        public ITrackerEditorExtension? CreateEditorExtension(string instanceId) => new PlainExtension();
    }

    private sealed class MemoryInstance(string instanceId, string displayName) : ITrackerInstance
    {
        public string PluginId => "test";
        public string InstanceId => instanceId;
        public string DisplayName => displayName;
        public string Icon => "memory";
        public bool IsConfigured => true;
        public IDictionary<int, object?>? LoadBindingsByDate(string date) => null;
    }

    private sealed class NoopPersistence : IWorkItemPersistenceCoordinator
    {
        public WorkItemSaveResult Save(
            Diary.Database.DbInterfaceBase db,
            WorkItemSaveRequest request)
            => new(false, false, null, "unused");
    }

    private sealed class NoopUpload : ITrackerUploadCoordinator
    {
        public Task<WorkUploadResult> UploadAsync(
            WorkItem item,
            IReadOnlyCollection<ITrackerEditorExtension> extensions)
            => Task.FromResult(new WorkUploadResult(Array.Empty<TrackerUploadResult>()));
    }
}
