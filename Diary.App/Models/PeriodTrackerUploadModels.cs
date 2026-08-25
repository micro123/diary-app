using Diary.Core.Data.Base;
using Diary.PluginBase;
using Diary.PluginUI;

namespace Diary.App.Models;

public enum PeriodTrackerUploadSkipKind
{
    Imported,
    Synchronized,
    Uncertain,
    TrackerIncomplete,
}

public sealed record PeriodTrackerUploadEligibility(
    bool CanUpload,
    PeriodTrackerUploadSkipKind? SkipKind = null,
    string? Detail = null)
{
    public static PeriodTrackerUploadEligibility Eligible { get; } = new(true);

    public static PeriodTrackerUploadEligibility Skip(
        PeriodTrackerUploadSkipKind kind,
        string detail) => new(false, kind, detail);
}

public static class PeriodTrackerUploadPolicy
{
    public static PeriodTrackerUploadEligibility Evaluate(
        WorkItem item,
        bool isImportedReadOnly,
        WorkItemUploadStatus uploadStatus,
        IReadOnlyCollection<ITrackerEditorExtension> extensions)
    {
        if (isImportedReadOnly)
            return PeriodTrackerUploadEligibility.Skip(
                PeriodTrackerUploadSkipKind.Imported,
                "迁移导入的只读事项");
        if (uploadStatus == WorkItemUploadStatus.Synchronized)
            return PeriodTrackerUploadEligibility.Skip(
                PeriodTrackerUploadSkipKind.Synchronized,
                "所有 Tracker 均已同步");
        if (uploadStatus == WorkItemUploadStatus.Uncertain
            || extensions.Any(extension => extension.UploadState is
                TrackerUploadState.Pending or TrackerUploadState.Uncertain))
        {
            return PeriodTrackerUploadEligibility.Skip(
                PeriodTrackerUploadSkipKind.Uncertain,
                "存在同步中或结果待确认的 Tracker");
        }
        if (item.Time <= 0)
        {
            return PeriodTrackerUploadEligibility.Skip(
                PeriodTrackerUploadSkipKind.TrackerIncomplete,
                "工时必须大于 0");
        }

        var targets = extensions.Where(extension => !extension.IsLocked).ToArray();
        if (targets.Length == 0)
        {
            return PeriodTrackerUploadEligibility.Skip(
                extensions.Count == 0
                    ? PeriodTrackerUploadSkipKind.TrackerIncomplete
                    : PeriodTrackerUploadSkipKind.Synchronized,
                extensions.Count == 0 ? "未配置 Tracker" : "所有 Tracker 均已同步");
        }

        foreach (var extension in targets)
        {
            var validation = extension.ValidateUpload(item);
            if (!validation.CanUpload)
            {
                var label = $"{extension.Key.PluginId}/{extension.Key.InstanceId}";
                return PeriodTrackerUploadEligibility.Skip(
                    PeriodTrackerUploadSkipKind.TrackerIncomplete,
                    string.IsNullOrWhiteSpace(validation.Error)
                        ? $"{label} 信息不完整"
                        : $"{label}: {validation.Error}");
            }
        }

        return PeriodTrackerUploadEligibility.Eligible;
    }
}

public sealed record PeriodTrackerUploadProgress(
    int Completed,
    int Total,
    int Succeeded,
    int Skipped,
    string Message);

public sealed record PeriodTrackerUploadFailure(
    int WorkItemId,
    string Date,
    string Title,
    string Error);

public sealed record PeriodTrackerUploadSummary(
    DateTime StartDate,
    DateTime EndDate,
    int Total,
    int Processed,
    int Succeeded,
    int Skipped,
    int Failed,
    IReadOnlyDictionary<PeriodTrackerUploadSkipKind, int> SkipCounts,
    PeriodTrackerUploadFailure? Failure)
{
    public int Unprocessed => Math.Max(0, Total - Processed);
}
