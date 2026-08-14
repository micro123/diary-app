using Diary.App.Models;
using Diary.ScriptBase;

namespace Diary.AppTests;

[TestClass]
public sealed class ScriptProgressTrackerTests
{
    [TestMethod]
    public void Report_StoresLatestSnapshotAndRaisesChanged()
    {
        var tracker = new ScriptProgressTracker();
        var raised = 0;
        tracker.Changed += (_, _) => raised++;

        tracker.Report("exec-1", new ScriptProgressUpdate(0.5, "一半"));

        var latest = tracker.Get("exec-1");
        Assert.IsNotNull(latest);
        Assert.AreEqual(0.5, latest!.Fraction);
        Assert.AreEqual("一半", latest.Message);
        Assert.IsNotNull(tracker.LastReported);
        Assert.AreEqual(1, raised);
    }

    [TestMethod]
    public void Report_AccumulatesTranscriptForExecution()
    {
        var tracker = new ScriptProgressTracker();
        tracker.Report("exec-1", new ScriptProgressUpdate(0.25, "第一步"));
        tracker.Report("exec-1", new ScriptProgressUpdate(0.75, "第二步"));
        tracker.Report("exec-2", new ScriptProgressUpdate(1.0, "另一个"));

        Assert.AreEqual(2, tracker.GetTranscript("exec-1").Count);
        Assert.AreEqual(1, tracker.GetTranscript("exec-2").Count);
        Assert.AreEqual("第二步", tracker.Get("exec-1")!.Message);
        Assert.IsEmpty(tracker.GetTranscript("missing"));
        Assert.IsNull(tracker.Get("missing"));
    }

    [TestMethod]
    public void Report_RejectsInvalidProgress()
    {
        var tracker = new ScriptProgressTracker();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            tracker.Report("exec-1", new ScriptProgressUpdate(1.5, "越界")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            tracker.Report("exec-1", new ScriptProgressUpdate(0.5, "  ")));
        Assert.ThrowsExactly<ArgumentException>(() =>
            tracker.Report("", new ScriptProgressUpdate(0.5, "空 ID")));
    }

    [TestMethod]
    public void Report_EvictsOldestExecutionsBeyondCapacity()
    {
        var tracker = new ScriptProgressTracker();
        for (var index = 0; index < ScriptProgressTracker.MaxExecutions + 2; index++)
            tracker.Report($"exec-{index}", new ScriptProgressUpdate(0.1, "进度"));

        Assert.IsNull(tracker.Get("exec-0"));
        Assert.IsNull(tracker.Get("exec-1"));
        Assert.IsNotNull(tracker.Get($"exec-{ScriptProgressTracker.MaxExecutions + 1}"));
    }

    [TestMethod]
    public void Clear_RemovesAllProgress()
    {
        var tracker = new ScriptProgressTracker();
        tracker.Report("exec-1", new ScriptProgressUpdate(0.5, "一半"));

        tracker.Clear();

        Assert.IsNull(tracker.Get("exec-1"));
        Assert.IsNull(tracker.LastReported);
    }
}
