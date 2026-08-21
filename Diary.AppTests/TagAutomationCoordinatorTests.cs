using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;

namespace Diary.AppTests;

[TestClass]
public sealed class TagAutomationCoordinatorTests
{
    [TestMethod]
    public void TagAddedAggregatesTrackerDefaults()
    {
        var expected = new TrackerTagDefaultsResult(
            ["ActivityIndex", "IssueIndex"],
            [new TrackerTagDefaultConflict("IssueIndex", ["rule-a", "rule-b"])],
            [new TrackerTagDefaultInvalidTarget("ActivityIndex", "99", "rule-c")]);
        var extension = new FakeTrackerExtension("redmine", "test", expected);
        var tag = new WorkTag { Id = 7, Name = "自动化标签" };

        var result = new TagAutomationCoordinator().TagAdded(
            null,
            tag,
            new TagAutomationContext(TagAddSource.Template, 0),
            [extension]);

        Assert.IsTrue(result.Succeeded);
        Assert.AreSame(tag, extension.LastTag);
        var instance = result.Instances.Single();
        Assert.AreEqual(extension.Key, instance.TrackerKey);
        CollectionAssert.AreEqual(expected.ChangedFields.ToArray(), instance.ChangedFields.ToArray());
        Assert.AreEqual(1, instance.Conflicts.Count);
        Assert.AreEqual(1, instance.InvalidTargets.Count);
    }

    [TestMethod]
    public void TagAddedIsolatesTrackerFailureAndContinues()
    {
        var failed = new FakeTrackerExtension("redmine", "failed", exception: new InvalidOperationException("规则失败"));
        var succeeded = new FakeTrackerExtension(
            "redmine",
            "succeeded",
            new TrackerTagDefaultsResult(["IssueIndex"], [], []));

        var result = new TagAutomationCoordinator().TagAdded(
            new WorkItem { Id = 42 },
            new WorkTag { Id = 8, Name = "继续执行标签" },
            new TagAutomationContext(TagAddSource.User, 1),
            [failed, succeeded]);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(2, result.Instances.Count);
        Assert.IsFalse(result.Instances.First().Succeeded);
        Assert.AreEqual("规则失败", result.Instances.First().Error);
        Assert.IsTrue(result.Instances.Last().Succeeded);
        CollectionAssert.AreEqual(
            new[] { "IssueIndex" },
            result.Instances.Last().ChangedFields.ToArray());
    }

    private sealed class FakeTrackerExtension : ViewModelBase, ITrackerEditorExtension, ITrackerTagDefaults
    {
        private readonly TrackerTagDefaultsResult _result;
        private readonly Exception? _exception;

        public FakeTrackerExtension(
            string pluginId,
            string instanceId,
            TrackerTagDefaultsResult? result = null,
            Exception? exception = null)
        {
            Key = new TrackerKey(pluginId, instanceId);
            _result = result ?? TrackerTagDefaultsResult.Empty;
            _exception = exception;
        }

        public TrackerKey Key { get; }
        public string InstanceId => Key.InstanceId;
        ViewModelBase ITrackerEditorExtension.View => this;
        public bool IsLocked => false;
        public bool CanDelete => true;
        public WorkTag? LastTag { get; private set; }

        public TrackerTagDefaultsResult ApplyTagDefaults(WorkTag tag)
        {
            LastTag = tag;
            if (_exception is not null)
                throw _exception;
            return _result;
        }

        public void Load(WorkItem? item, object? binding = null)
        {
        }

        public bool Save(WorkItem item) => true;

        public void CloneTo(ITrackerEditorExtension? target)
        {
        }

        public Task<TrackerOperationResult> UploadAsync(WorkItem item)
            => Task.FromResult(new TrackerOperationResult(true));
    }
}
