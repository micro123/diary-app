namespace Diary.App.Models;

public enum WorkItemUploadStatus
{
    Unsaved,
    NotConfigured,
    Pending,
    Synchronized,
    PartialFailure,
    Failed,
    Uncertain,
}

public static class WorkItemUploadStatusResolver
{
    public static WorkItemUploadStatus Resolve(
        bool isSaved,
        int trackerCount,
        int lockedTrackerCount,
        bool hasUploadFailure,
        bool hasUploadUncertain = false)
    {
        if (!isSaved)
            return WorkItemUploadStatus.Unsaved;
        if (trackerCount == 0)
            return WorkItemUploadStatus.NotConfigured;
        if (hasUploadUncertain)
            return WorkItemUploadStatus.Uncertain;
        if (hasUploadFailure)
            return lockedTrackerCount > 0
                ? WorkItemUploadStatus.PartialFailure
                : WorkItemUploadStatus.Failed;
        return lockedTrackerCount == trackerCount
            ? WorkItemUploadStatus.Synchronized
            : WorkItemUploadStatus.Pending;
    }

    public static string GetDisplayText(WorkItemUploadStatus status) => status switch
    {
        WorkItemUploadStatus.Unsaved => "待保存",
        WorkItemUploadStatus.NotConfigured => "未配置 Tracker",
        WorkItemUploadStatus.Pending => "待同步",
        WorkItemUploadStatus.Synchronized => "已同步",
        WorkItemUploadStatus.PartialFailure => "部分同步，存在失败",
        WorkItemUploadStatus.Failed => "同步失败",
        WorkItemUploadStatus.Uncertain => "同步结果待确认",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
