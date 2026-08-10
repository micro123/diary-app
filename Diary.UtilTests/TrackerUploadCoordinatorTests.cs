using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;

namespace Diary.UtilTests;

[TestClass]
public sealed class TrackerUploadCoordinatorTests
{
    [TestMethod]
    public async Task UploadContinuesAfterOneTrackerFails()
    {
        var first = new FakeExtension(new TrackerKey("tracker.one", "default"),
            new TrackerOperationResult(false, "network"));
        var second = new FakeExtension(new TrackerKey("tracker.two", "default"),
            new TrackerOperationResult(true, remoteId: "42"));

        var result = await new TrackerUploadCoordinator().UploadAsync(
            new WorkItem { Id = 1 }, new[] { first, second });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "tracker.one/default");
        Assert.AreEqual(2, result.Results.Count);
        Assert.IsFalse(result.Results[0].Success);
        Assert.IsTrue(result.Results[1].Success);
        Assert.AreEqual(1, first.UploadCount);
        Assert.AreEqual(1, second.UploadCount);
    }

    [TestMethod]
    public async Task UploadSkipsLockedTracker()
    {
        var locked = new FakeExtension(
            new TrackerKey("tracker.one", "default"),
            new TrackerOperationResult(true), locked: true);
        var open = new FakeExtension(
            new TrackerKey("tracker.two", "default"),
            new TrackerOperationResult(true));

        var result = await new TrackerUploadCoordinator().UploadAsync(
            new WorkItem { Id = 1 }, new[] { locked, open });

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Results[0].Skipped);
        Assert.IsFalse(result.Results[1].Skipped);
        Assert.AreEqual(0, locked.UploadCount);
        Assert.AreEqual(1, open.UploadCount);
    }

    [TestMethod]
    public async Task UploadMarksThrownTrackerFailureAsUncertain()
    {
        var extension = new ThrowingExtension(new TrackerKey("tracker.one", "default"));

        var result = await new TrackerUploadCoordinator().UploadAsync(
            new WorkItem { Id = 1 }, new[] { extension });

        Assert.IsFalse(result.Success);
        Assert.AreEqual(TrackerUploadState.Uncertain, result.Results[0].State);
    }

    private sealed class FakeExtension(
        TrackerKey key,
        TrackerOperationResult uploadResult,
        bool locked = false) : ITrackerEditorExtension
    {
        public TrackerKey Key => key;
        public string InstanceId => key.InstanceId;
        public ViewModelBase View => new();
        public bool IsLocked => locked;
        public bool CanDelete => true;
        public int UploadCount { get; private set; }

        public void Load(WorkItem? item, object? binding = null) { }
        public bool Save(WorkItem item) => true;
        public void CloneTo(ITrackerEditorExtension? target) { }
        public Task<TrackerOperationResult> UploadAsync(WorkItem item)
        {
            UploadCount++;
            return Task.FromResult(uploadResult);
        }
    }

    private sealed class ThrowingExtension(TrackerKey key) : ITrackerEditorExtension
    {
        public TrackerKey Key => key;
        public string InstanceId => key.InstanceId;
        public ViewModelBase View => new();
        public bool IsLocked => false;
        public bool CanDelete => true;

        public void Load(WorkItem? item, object? binding = null) { }
        public bool Save(WorkItem item) => true;
        public void CloneTo(ITrackerEditorExtension? target) { }
        public Task<TrackerOperationResult> UploadAsync(WorkItem item)
            => throw new InvalidOperationException("模拟远程异常");
    }
}
