using Diary.App;
using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;

namespace Diary.AppTests;

[TestClass]
public sealed class WorkItemTagAutomationServiceTests
{
    [TestMethod]
    public void PrepareCreateLoadsExtensionAndAppliesTagDefaultsWithoutRemoteUpload()
    {
        var instance = new RecordingTrackerInstance();
        var factory = new RecordingContributionFactory();
        using var registry = new TrackerUiContributionRegistry();
        registry.Register([factory], [instance]);
        var service = new WorkItemTagAutomationService(registry, new TagAutomationCoordinator());
        var tag = new WorkTag { Id = 42, Name = "Redmine 自动化" };

        using var preparation = service.Prepare(null, [tag], TagAddSource.Agent);

        Assert.IsTrue(preparation.Succeeded, preparation.Error);
        Assert.AreEqual(1, preparation.Extensions.Count);
        var extension = factory.LastContribution!.LastExtension!;
        Assert.IsTrue(extension.LoadedForCreate);
        CollectionAssert.AreEqual(new[] { tag.Id }, extension.AppliedTagIds.ToArray());
        Assert.AreEqual(0, extension.UploadCount);
    }

    [TestMethod]
    public void PrepareUpdateUsesBatchLoadedBindingForExistingItem()
    {
        var binding = new object();
        var instance = new RecordingTrackerInstance(binding);
        var factory = new RecordingContributionFactory();
        using var registry = new TrackerUiContributionRegistry();
        registry.Register([factory], [instance]);
        var service = new WorkItemTagAutomationService(registry, new TagAutomationCoordinator());
        var item = new WorkItem { Id = 7, CreateDate = "2026-10-09" };

        using var preparation = service.Prepare(
            item,
            [new WorkTag { Id = 43, Name = "新增标签" }],
            TagAddSource.Agent);

        Assert.IsTrue(preparation.Succeeded, preparation.Error);
        Assert.AreSame(item, factory.LastContribution!.LastExtension!.LoadedItem);
        Assert.AreSame(binding, factory.LastContribution.LastExtension.LoadedBinding);
        CollectionAssert.AreEqual(new[] { item.Id }, instance.LastRequestedWorkItemIds);
    }

    private sealed class RecordingContributionFactory : ITrackerUiContributionFactory
    {
        public string PluginId => "redmine-test";
        public RecordingContribution? LastContribution { get; private set; }

        public ITrackerUiContribution Create(ITrackerInstance instance)
            => LastContribution = new RecordingContribution(instance);
    }

    private sealed class RecordingContribution(ITrackerInstance instance) : ITrackerUiContribution
    {
        public string PluginId => instance.PluginId;
        public ITrackerInstance Instance => instance;
        public RecordingExtension? LastExtension { get; private set; }

        public ViewModelBase? CreateSettingsPage(object configuration) => null;
        public ViewModelBase? CreateManagementPage(string instanceId) => null;

        public ITrackerEditorExtension? CreateEditorExtension(string instanceId)
            => LastExtension = new RecordingExtension(instanceId);
    }

    private sealed class RecordingTrackerInstance(object? binding = null) : ITrackerInstance
    {
        public string PluginId => "redmine-test";
        public string InstanceId => "company";
        public string DisplayName => "公司 Redmine";
        public string Icon => "cloud";
        public bool IsConfigured => true;
        public int[] LastRequestedWorkItemIds { get; private set; } = [];

        public IDictionary<int, object?>? LoadBindingsByDate(string date) => null;

        public IDictionary<int, object?>? LoadBindingsByDate(
            string date,
            IReadOnlyCollection<int> workItemIds)
        {
            LastRequestedWorkItemIds = workItemIds.ToArray();
            return binding is null ? null : new Dictionary<int, object?> { [workItemIds.Single()] = binding };
        }
    }

    private sealed class RecordingExtension(string instanceId) : ViewModelBase, ITrackerEditorExtension, ITrackerTagDefaults
    {
        public TrackerKey Key => new("redmine-test", instanceId);
        public string InstanceId => instanceId;
        ViewModelBase ITrackerEditorExtension.View => this;
        public bool IsLocked => false;
        public bool CanDelete => true;
        public bool LoadedForCreate { get; private set; }
        public WorkItem? LoadedItem { get; private set; }
        public object? LoadedBinding { get; private set; }
        public List<int> AppliedTagIds { get; } = [];
        public int UploadCount { get; private set; }

        public void Load(WorkItem? item, object? binding = null)
        {
            LoadedForCreate = item is null;
            LoadedItem = item;
            LoadedBinding = binding;
        }

        public void LoadFromBatch(WorkItem? item, object? binding)
        {
            LoadedItem = item;
            LoadedBinding = binding;
        }

        public TrackerTagDefaultsResult ApplyTagDefaults(WorkTag tag)
        {
            AppliedTagIds.Add(tag.Id);
            return new TrackerTagDefaultsResult(["IssueIndex", "ActivityIndex"], [], []);
        }

        public bool Save(WorkItem item) => true;
        public void CloneTo(ITrackerEditorExtension? target) { }

        public Task<TrackerOperationResult> UploadAsync(WorkItem item)
        {
            UploadCount++;
            return Task.FromResult(new TrackerOperationResult(true));
        }
    }
}
