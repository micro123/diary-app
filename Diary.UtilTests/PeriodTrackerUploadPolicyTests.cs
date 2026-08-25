using Diary.App.Models;
using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;

namespace Diary.UtilTests;

[TestClass]
public sealed class PeriodTrackerUploadPolicyTests
{
    [TestMethod]
    public void Evaluate_SkipsImportedWorkItem()
    {
        var result = Evaluate(isImported: true);

        Assert.IsFalse(result.CanUpload);
        Assert.AreEqual(PeriodTrackerUploadSkipKind.Imported, result.SkipKind);
    }

    [TestMethod]
    public void Evaluate_SkipsSynchronizedWorkItem()
    {
        var result = Evaluate(uploadStatus: WorkItemUploadStatus.Synchronized);

        Assert.IsFalse(result.CanUpload);
        Assert.AreEqual(PeriodTrackerUploadSkipKind.Synchronized, result.SkipKind);
    }

    [TestMethod]
    public void Evaluate_SkipsPendingOrUncertainTrackerState()
    {
        var result = Evaluate(extension: new PolicyExtension(
            TrackerUploadState.Uncertain,
            TrackerUploadValidation.Valid));

        Assert.IsFalse(result.CanUpload);
        Assert.AreEqual(PeriodTrackerUploadSkipKind.Uncertain, result.SkipKind);
    }

    [TestMethod]
    public void Evaluate_SkipsZeroHourWorkItem()
    {
        var result = Evaluate(hours: 0);

        Assert.IsFalse(result.CanUpload);
        Assert.AreEqual(PeriodTrackerUploadSkipKind.TrackerIncomplete, result.SkipKind);
        StringAssert.Contains(result.Detail, "工时");
    }

    [TestMethod]
    public void Evaluate_SkipsIncompleteTrackerBinding()
    {
        var result = Evaluate(extension: new PolicyExtension(
            TrackerUploadState.NotAttempted,
            TrackerUploadValidation.Invalid("未设置 Issue")));

        Assert.IsFalse(result.CanUpload);
        Assert.AreEqual(PeriodTrackerUploadSkipKind.TrackerIncomplete, result.SkipKind);
        StringAssert.Contains(result.Detail, "未设置 Issue");
    }

    [TestMethod]
    public void Evaluate_SkipsWhenNoTrackerIsConfigured()
    {
        var result = PeriodTrackerUploadPolicy.Evaluate(
            new WorkItem { Id = 1, Time = 1 },
            isImportedReadOnly: false,
            WorkItemUploadStatus.NotConfigured,
            Array.Empty<ITrackerEditorExtension>());

        Assert.IsFalse(result.CanUpload);
        Assert.AreEqual(PeriodTrackerUploadSkipKind.TrackerIncomplete, result.SkipKind);
        StringAssert.Contains(result.Detail, "未配置 Tracker");
    }

    [TestMethod]
    public void Evaluate_SkipsWholeWorkItemWhenOnePendingTrackerIsIncomplete()
    {
        var result = PeriodTrackerUploadPolicy.Evaluate(
            new WorkItem { Id = 1, Time = 1 },
            isImportedReadOnly: false,
            WorkItemUploadStatus.Pending,
            [
                new PolicyExtension(TrackerUploadState.NotAttempted, TrackerUploadValidation.Valid),
                new PolicyExtension(
                    TrackerUploadState.NotAttempted,
                    TrackerUploadValidation.Invalid("未设置活动"),
                    instanceId: "secondary"),
            ]);

        Assert.IsFalse(result.CanUpload);
        Assert.AreEqual(PeriodTrackerUploadSkipKind.TrackerIncomplete, result.SkipKind);
        StringAssert.Contains(result.Detail, "secondary");
    }

    [TestMethod]
    public void Evaluate_AllowsValidFailedTrackerForRetry()
    {
        var result = Evaluate(
            uploadStatus: WorkItemUploadStatus.Failed,
            extension: new PolicyExtension(
                TrackerUploadState.Failed,
                TrackerUploadValidation.Valid));

        Assert.IsTrue(result.CanUpload);
        Assert.IsNull(result.SkipKind);
    }

    private static PeriodTrackerUploadEligibility Evaluate(
        bool isImported = false,
        double hours = 1,
        WorkItemUploadStatus uploadStatus = WorkItemUploadStatus.Pending,
        ITrackerEditorExtension? extension = null)
        => PeriodTrackerUploadPolicy.Evaluate(
            new WorkItem { Id = 1, Time = hours },
            isImported,
            uploadStatus,
            [extension ?? new PolicyExtension(
                TrackerUploadState.NotAttempted,
                TrackerUploadValidation.Valid)]);

    private sealed class PolicyExtension(
        TrackerUploadState state,
        TrackerUploadValidation validation,
        string instanceId = "default") : ITrackerEditorExtension
    {
        public TrackerKey Key { get; } = new("tracker.test", instanceId);
        public string InstanceId => Key.InstanceId;
        public ViewModelBase View { get; } = new();
        public bool IsLocked => false;
        public TrackerUploadState UploadState => state;
        public bool CanDelete => true;

        public void Load(WorkItem? item, object? binding = null)
        {
        }

        public bool Save(WorkItem item) => true;

        public void CloneTo(ITrackerEditorExtension? target)
        {
        }

        public TrackerUploadValidation ValidateUpload(WorkItem item) => validation;

        public Task<TrackerOperationResult> UploadAsync(WorkItem item)
            => Task.FromResult(new TrackerOperationResult(true));
    }
}
