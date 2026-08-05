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
    public void Coordinator_AppliesDefaultsOnlyToCapableExtensions()
    {
        var capable = new TagDefaultsExtension();
        var incapable = new PlainExtension();

        new TagAutomationCoordinator().TagAdded(
            null,
            new WorkTag { Id = 7 },
            new TagAutomationContext(TagAddSource.User, 0),
            new ITrackerEditorExtension[] { capable, incapable });

        Assert.AreEqual(7, capable.AppliedTagId);
    }

    private static WorkEditorViewModel CreateEditor(ITagAutomationCoordinator automation)
        => new(
            new DbShareData(NullLogger.Instance),
            new NoopPersistence(),
            new NoopUpload(),
            new TrackerUiContributionRegistry(),
            "test",
            automation);

    private sealed class RecordingAutomation : ITagAutomationCoordinator
    {
        public List<int> Tags { get; } = new();
        public List<int> Sequences { get; } = new();
        public List<TagAddSource> Sources { get; } = new();

        public void TagAdded(
            WorkItem? item,
            WorkTag tag,
            TagAutomationContext context,
            IReadOnlyCollection<ITrackerEditorExtension> extensions)
        {
            Tags.Add(tag.Id);
            Sequences.Add(context.Sequence);
            Sources.Add(context.Source);
        }
    }

    private class PlainExtension : ITrackerEditorExtension
    {
        public TrackerKey Key => new("test", "default");
        public string InstanceId => "default";
        public Diary.GUIBase.ViewModels.ViewModelBase View { get; } = new();
        public bool IsLocked => false;
        public bool CanDelete => true;
        public void Load(WorkItem? item, object? binding = null) { }
        public bool Save(WorkItem item) => true;
        public void CloneTo(ITrackerEditorExtension? target) { }
        public Task<TrackerOperationResult> UploadAsync(WorkItem item)
            => Task.FromResult(new TrackerOperationResult(false));
        public void ApplyTemplateData(object data) { }
    }

    private sealed class TagDefaultsExtension : PlainExtension, ITrackerTagDefaults
    {
        public int AppliedTagId { get; private set; }

        public IReadOnlyCollection<string> ApplyTagDefaults(WorkTag tag)
        {
            AppliedTagId = tag.Id;
            return Array.Empty<string>();
        }
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
