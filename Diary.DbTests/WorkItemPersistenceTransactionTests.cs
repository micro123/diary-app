using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;

namespace Diary.DbTests;

[TestClass]
public sealed class WorkItemPersistenceTransactionTests
{
    [TestMethod]
    public void MultipleTrackerExtensionsCommitTogether()
    {
        using var db = TestDb.Create();
        var first = new FakeExtension("tracker.one", true);
        var second = new FakeExtension("tracker.two", true);

        var result = Save(db, first, second);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, db.GetWorkItemByDate("2026-08-05").Count);
        Assert.AreEqual(1, first.SaveCount);
        Assert.AreEqual(1, second.SaveCount);
    }

    [TestMethod]
    public void FailedTrackerExtensionRollsBackCoreAndOtherTrackerChanges()
    {
        using var db = TestDb.Create();
        var first = new FakeExtension("tracker.one", true);
        var second = new FakeExtension("tracker.two", false);

        var result = Save(db, first, second);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "tracker.two");
        Assert.AreEqual(0, db.GetWorkItemByDate("2026-08-05").Count);
        Assert.AreEqual(1, first.SaveCount);
        Assert.AreEqual(1, second.SaveCount);
    }

    [TestMethod]
    public void FailedItemInBatchRollsBackAllItems()
    {
        using var db = TestDb.Create();
        var first = new FakeExtension("tracker.one", true);
        var second = new FakeExtension("tracker.two", false);
        var requests = new[]
        {
            CreateRequest("2026-08-05", "批量事务 A", first),
            CreateRequest("2026-08-06", "批量事务 B", second),
        };

        var result = new WorkItemPersistenceCoordinator().SaveBatch(db, requests);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "tracker.two");
        Assert.AreEqual(0, db.GetWorkItemByDate("2026-08-05").Count);
        Assert.AreEqual(0, db.GetWorkItemByDate("2026-08-06").Count);
        Assert.AreEqual(1, first.SaveCount);
        Assert.AreEqual(1, second.SaveCount);
    }

    private static WorkItemSaveResult Save(
        Diary.Database.DbInterfaceBase db,
        params ITrackerEditorExtension[] extensions)
        => new WorkItemPersistenceCoordinator().Save(
            db,
            CreateRequest("2026-08-05", "事务测试", extensions));

    private static WorkItemSaveRequest CreateRequest(
        string date,
        string comment,
        params ITrackerEditorExtension[] extensions) => new(
            Existing: null,
            Date: date,
            Comment: comment,
            Note: "多 tracker 本地事务",
            Time: 1,
            Priority: WorkPriorities.P2,
            Tags: Array.Empty<WorkTag>(),
            ExtraFieldValues: Array.Empty<WorkItemExtraFieldValue>(),
            Extensions: extensions);

    private sealed class FakeExtension(string pluginId, bool saveResult) : ITrackerEditorExtension
    {
        public TrackerKey Key => new(pluginId, "default");
        public string InstanceId => "default";
        public ViewModelBase View => new();
        public bool IsLocked => false;
        public bool CanDelete => true;
        public int SaveCount { get; private set; }

        public void Load(WorkItem? item, object? binding = null) { }

        public bool Save(WorkItem item)
        {
            SaveCount++;
            return saveResult;
        }

        public void CloneTo(ITrackerEditorExtension? target) { }
        public Task<TrackerOperationResult> UploadAsync(WorkItem item)
            => Task.FromResult(new TrackerOperationResult(true));
    }
}
