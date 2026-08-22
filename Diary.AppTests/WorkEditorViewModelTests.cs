using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Diary.App;
using Diary.App.Models;
using Diary.App.ViewModels;
using Diary.App.ViewModels.Dialogs;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.PluginBase;
using Diary.PluginUI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diary.AppTests;

[TestClass]
[DoNotParallelize]
public sealed class WorkEditorViewModelTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _session = HeadlessUnitTestSession.StartNew(typeof(TestApplication));
    }

    [ClassCleanup]
    public static void Cleanup() => _session.Dispose();

    [TestMethod]
    public async Task UploadFromBackgroundThreadUpdatesResultsOnUiThread()
    {
        var coordinator = new RecordingUploadCoordinator();
        var viewModel = new WorkEditorViewModel(
            new DbShareData(NullLogger<DbShareData>.Instance),
            new NoopPersistenceCoordinator(),
            coordinator,
            new TrackerUiContributionRegistry(),
            "测试工作项",
            new NoopTagAutomationCoordinator());
        SetWorkItem(viewModel, new WorkItem
        {
            Id = 42,
            CreateDate = "2026-08-11",
            Comment = "测试工作项",
            Time = 1.5,
        });

        await _session.Dispatch(async () =>
        {
            var result = await Task.Run(viewModel.Upload);

            Assert.IsTrue(result.Item1);
            Assert.AreEqual(1, viewModel.UploadResults.Count);
            Assert.AreEqual(TrackerUploadState.Succeeded, viewModel.UploadResults[0].State);
            Assert.AreEqual(42, coordinator.LastItem?.Id);
        }, CancellationToken.None);
    }

    [TestMethod]
    public void ImportedExtraFieldsAreReadOnlyInDialog()
    {
        var field = new WorkItemExtraField
        {
            FieldId = "field-id",
            FieldKey = "legacy.value",
            TagId = 7,
            TagName = "旧标签",
            Label = "旧值",
            Type = TagExtraFieldType.Text,
            Value = "历史值",
        };
        var viewModel = new WorkItemExtraFieldsViewModel(null!, 42, [field], isReadOnly: true);

        Assert.IsTrue(viewModel.IsReadOnly);
        Assert.AreEqual("查看附加信息", viewModel.Title);
        Assert.IsTrue(viewModel.Groups.Single().Fields.Single().IsReadOnly);
        Assert.IsFalse(viewModel.SaveCommand.CanExecute(null));
    }

    [TestMethod]
    public void PristineNewItemDoesNotNeedPersistenceBeforeReplacement()
    {
        var viewModel = CreateViewModel();

        Assert.IsFalse(viewModel.ShouldPersistBeforeReplacement);
    }

    [TestMethod]
    public void EditedNewItemNeedsPersistenceBeforeReplacement()
    {
        var title = CreateViewModel();
        title.Comment = "新建后输入标题";
        var note = CreateViewModel();
        note.Note = "备注";
        var time = CreateViewModel();
        time.Time = 0.25;
        var priority = CreateViewModel();
        priority.Priority = WorkPriorities.P1;

        Assert.IsTrue(title.ShouldPersistBeforeReplacement);
        Assert.IsTrue(note.ShouldPersistBeforeReplacement);
        Assert.IsTrue(time.ShouldPersistBeforeReplacement);
        Assert.IsTrue(priority.ShouldPersistBeforeReplacement);
    }

    [TestMethod]
    public void ExistingItemNeedsPersistenceBeforeReplacement()
    {
        var viewModel = CreateViewModel();
        SetWorkItem(viewModel, new WorkItem
        {
            Id = 7,
            CreateDate = "2026-08-21",
            Comment = string.Empty,
        });

        Assert.IsTrue(viewModel.ShouldPersistBeforeReplacement);
    }

    [TestMethod]
    public void ImportedReadOnlyItemDoesNotNeedPersistenceBeforeReplacement()
    {
        var viewModel = CreateViewModel();
        SetWorkItem(viewModel, new WorkItem
        {
            Id = 8,
            CreateDate = "2026-08-22",
            Comment = "迁移记录",
            IsReadOnly = true,
        });

        Assert.IsFalse(viewModel.ShouldPersistBeforeReplacement);
    }

    [TestMethod]
    public void TrackerUploadResultIncludesRecoveryDetails()
    {
        var attemptedAt = new DateTimeOffset(2026, 8, 11, 14, 0, 0, TimeSpan.FromHours(8));
        var result = new TrackerUploadResult(
            new TrackerKey("test", "local"),
            false,
            false,
            "网络连接中断",
            State: TrackerUploadState.Uncertain,
            AttemptedAt: attemptedAt);

        StringAssert.Contains(result.ResultSummary, "请先核对远程记录后再决定是否重试");
        var expectedAttemptedAt = attemptedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        StringAssert.Contains(result.ResultSummary, $"尝试时间：{expectedAttemptedAt}");
    }

    private static WorkEditorViewModel CreateViewModel()
        => new(
            new DbShareData(NullLogger<DbShareData>.Instance),
            new NoopPersistenceCoordinator(),
            new RecordingUploadCoordinator(),
            new TrackerUiContributionRegistry(),
            string.Empty,
            new NoopTagAutomationCoordinator());
    private static void SetWorkItem(WorkEditorViewModel viewModel, WorkItem item)
    {
        var property = typeof(WorkEditorViewModel).GetProperty(
            "WorkItem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(property);
        property.SetValue(viewModel, item);
    }

    private sealed class RecordingUploadCoordinator : ITrackerUploadCoordinator
    {
        public WorkItem? LastItem { get; private set; }

        public Task<WorkUploadResult> UploadAsync(
            WorkItem item,
            IReadOnlyCollection<ITrackerEditorExtension> extensions)
        {
            LastItem = item;
            return Task.FromResult(new WorkUploadResult([
                new TrackerUploadResult(
                    new TrackerKey("test", "local"),
                    true,
                    false,
                    State: TrackerUploadState.Succeeded),
            ]));
        }
    }

    private sealed class NoopPersistenceCoordinator : IWorkItemPersistenceCoordinator
    {
        public WorkItemSaveResult Save(DbInterfaceBase db, WorkItemSaveRequest request)
            => new(false, false, Error: "测试不应保存工作项");
    }

    private sealed class NoopTagAutomationCoordinator : ITagAutomationCoordinator
    {
        public TagAutomationResult TagAdded(
            WorkItem? item,
            WorkTag tag,
            TagAutomationContext context,
            IReadOnlyCollection<ITrackerEditorExtension> extensions)
            => new(Array.Empty<TagAutomationInstanceResult>());
    }

    private sealed class TestApplication : Application
    {
    }
}
