using Diary.App.Models;

namespace Diary.UtilTests;

[TestClass]
public sealed class WorkItemUploadStatusTests
{
    [TestMethod]
    [DataRow(false, 0, 0, false, WorkItemUploadStatus.Unsaved)]
    [DataRow(true, 0, 0, false, WorkItemUploadStatus.NotConfigured)]
    [DataRow(true, 2, 0, false, WorkItemUploadStatus.Pending)]
    [DataRow(true, 2, 2, false, WorkItemUploadStatus.Synchronized)]
    [DataRow(true, 2, 0, true, WorkItemUploadStatus.Failed)]
    [DataRow(true, 2, 1, true, WorkItemUploadStatus.PartialFailure)]
    public void Resolve_ReturnsExpectedStatus(
        bool isSaved,
        int trackerCount,
        int lockedTrackerCount,
        bool hasUploadFailure,
        WorkItemUploadStatus expected)
    {
        var actual = WorkItemUploadStatusResolver.Resolve(
            isSaved,
            trackerCount,
            lockedTrackerCount,
            hasUploadFailure);

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow(WorkItemUploadStatus.Unsaved, "待保存")]
    [DataRow(WorkItemUploadStatus.NotConfigured, "未配置 Tracker")]
    [DataRow(WorkItemUploadStatus.Pending, "待同步")]
    [DataRow(WorkItemUploadStatus.Synchronized, "已同步")]
    [DataRow(WorkItemUploadStatus.PartialFailure, "部分同步，存在失败")]
    [DataRow(WorkItemUploadStatus.Failed, "同步失败")]
    public void GetDisplayText_ReturnsUserFacingText(WorkItemUploadStatus status, string expected)
    {
        Assert.AreEqual(expected, WorkItemUploadStatusResolver.GetDisplayText(status));
    }
}
