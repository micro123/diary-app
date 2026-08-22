using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Diary.App;
using Diary.App.Models;
using Diary.App.ViewModels;
using Diary.App.ViewModels.Dialogs;
using Diary.Core.Data.Base;
using Diary.Database;
using Diary.Db.SQLite;
using Diary.GUIBase.ViewModels;
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
    public void UnifiedTimeInputParsesExpressionsAndNormalizesSameValue()
    {
        var viewModel = CreateViewModel();
        viewModel.Time = 1.5;
        viewModel.TimeInput = "90m";

        viewModel.ApplyTimeInputCommand.Execute(null);

        Assert.AreEqual(1.5, viewModel.Time, 0.0001);
        Assert.AreEqual("1.5", viewModel.TimeInput);
    }

    [TestMethod]
    public void UnifiedTimeInputSynchronizesQuickValuesAndCanResetEdits()
    {
        var viewModel = CreateViewModel();

        viewModel.QuickTimeCommand.Execute("30m");

        Assert.AreEqual(0.5, viewModel.Time, 0.0001);
        Assert.AreEqual("0.5", viewModel.TimeInput);

        viewModel.QuickTimeCommand.Execute("8h");
        Assert.AreEqual(8.0, viewModel.Time, 0.0001);
        Assert.AreEqual("8", viewModel.TimeInput);

        viewModel.TimeInput = "尚未应用";
        viewModel.ResetTimeInputCommand.Execute(null);

        Assert.AreEqual("8", viewModel.TimeInput);
    }

    [TestMethod]
    public void InvalidUnifiedTimeInputDoesNotOverwriteCommittedTime()
    {
        var viewModel = CreateViewModel();
        viewModel.Time = 2;
        viewModel.TimeInput = "invalid";

        viewModel.ApplyTimeInputCommand.Execute(null);

        Assert.AreEqual(2, viewModel.Time, 0.0001);
        Assert.AreEqual("invalid", viewModel.TimeInput);
    }

    [TestMethod]
    public void DeleteFailureKeepsPersistedWorkItemInEditor()
    {
        using var database = CreateDatabase();
        var item = database.CreateWorkItem("2026-08-22", "待删除");
        var viewModel = CreateViewModel(database: database);
        SetWorkItem(viewModel, item);
        Assert.IsTrue(database.DeleteWorkItem(item));

        Assert.IsFalse(viewModel.Delete());
        Assert.AreSame(item, GetWorkItem(viewModel));
        Assert.IsTrue(viewModel.ShouldPersistBeforeReplacement);
    }

    [TestMethod]
    public void RemoveTagFailureKeepsTagInEditor()
    {
        using var database = CreateDatabase();
        var item = database.CreateWorkItem("2026-08-22", "只读记录");
        var tag = database.CreateWorkTag("保留标签", false, 1);
        Assert.IsTrue(database.WorkItemAddTag(item, tag));
        Assert.IsTrue(database.MarkWorkItemReadOnly(item));
        item.IsReadOnly = true;

        var viewModel = CreateViewModel(database: database);
        SetWorkItem(viewModel, item);
        viewModel.WorkTags.Add(tag);

        viewModel.DelTagCommand.Execute(tag);

        CollectionAssert.Contains(viewModel.WorkTags, tag);
        Assert.IsTrue(database.GetWorkItemTags(item).Any(persisted => persisted.Id == tag.Id));
    }

    [TestMethod]
    public void EditableWorkTagRenamePersistsToDatabase()
    {
        using var database = CreateDatabase();
        var tag = database.CreateWorkTag("旧名称", true, 1);
        var editable = new EditableWorkTag(tag, database) { Name = "新名称" };

        Assert.IsTrue(editable.ApplyChanges(out var error), error);
        Assert.AreEqual("新名称", tag.Name);
        Assert.AreEqual("新名称", database.AllWorkTags().Single().Name);
    }

    [TestMethod]
    public void EditableWorkTagDuplicateNameFailureKeepsOriginalModel()
    {
        using var database = CreateDatabase();
        var tag = database.CreateWorkTag("原名称", true, 1);
        database.CreateWorkTag("已存在", false, 2);
        var editable = new EditableWorkTag(tag, database) { Name = "已存在" };

        Assert.IsFalse(editable.ApplyChanges(out var error));
        StringAssert.Contains(error, "重复");
        Assert.AreEqual("原名称", tag.Name);
        Assert.AreEqual("原名称", database.AllWorkTags().Single(item => item.Id == tag.Id).Name);
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
    public void CloneWithoutTrackerBindingsInitializesOptionsAndSkipsSelection()
    {
        var viewModel = CreateViewModel(CreateCloneTrackerRegistry());
        var source = (CloneTrackerExtension)viewModel.Extensions.Single();
        source.Load(null);
        source.Selection = "ISSUE-1";

        var clone = viewModel.Clone(includeTrackerBindings: false);
        var target = (CloneTrackerExtension)clone.Extensions.Single();

        Assert.IsTrue(target.OptionsLoaded);
        Assert.IsNull(target.Selection);
        Assert.AreEqual(0, source.CloneCallCount);
    }

    [TestMethod]
    public void CloneWithTrackerBindingsInitializesTargetBeforeCopyingSelection()
    {
        var viewModel = CreateViewModel(CreateCloneTrackerRegistry());
        var source = (CloneTrackerExtension)viewModel.Extensions.Single();
        source.Load(null);
        source.Selection = "ISSUE-1";

        var clone = viewModel.Clone();
        var target = (CloneTrackerExtension)clone.Extensions.Single();

        Assert.IsTrue(target.OptionsLoaded);
        Assert.IsTrue(target.OptionsLoadedWhenCloned);
        Assert.AreEqual("ISSUE-1", target.Selection);
        Assert.AreEqual(1, source.CloneCallCount);
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

    private static WorkEditorViewModel CreateViewModel(
        TrackerUiContributionRegistry? trackerRegistry = null,
        DbInterfaceBase? database = null)
        => new(
            new DbShareData(NullLogger<DbShareData>.Instance),
            new NoopPersistenceCoordinator(),
            new RecordingUploadCoordinator(),
            trackerRegistry ?? new TrackerUiContributionRegistry(),
            string.Empty,
            new NoopTagAutomationCoordinator(),
            database: database);

    private static SQLiteDb CreateDatabase()
    {
        var database = new SQLiteDb(new TestSqliteFactory());
        Assert.IsTrue(database.Connect());
        Assert.IsTrue(database.Initialized());
        return database;
    }

    private static TrackerUiContributionRegistry CreateCloneTrackerRegistry()
    {
        var registry = new TrackerUiContributionRegistry();
        registry.Register([new CloneTrackerContributionFactory()], [new CloneTrackerInstance()]);
        return registry;
    }
    private static void SetWorkItem(WorkEditorViewModel viewModel, WorkItem item)
    {
        var property = typeof(WorkEditorViewModel).GetProperty(
            "WorkItem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(property);
        property.SetValue(viewModel, item);
    }

    private static WorkItem? GetWorkItem(WorkEditorViewModel viewModel)
    {
        var property = typeof(WorkEditorViewModel).GetProperty(
            "WorkItem",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(property);
        return property.GetValue(viewModel) as WorkItem;
    }

    private sealed class CloneTrackerInstance : ITrackerInstance
    {
        public string PluginId => "test.clone";
        public string InstanceId => "local";
        public string DisplayName => "克隆测试";
        public string Icon => string.Empty;
        public bool IsConfigured => true;
        public IDictionary<int, object?>? LoadBindingsByDate(string date) => null;
    }

    private sealed class CloneTrackerContributionFactory : ITrackerUiContributionFactory
    {
        public string PluginId => "test.clone";
        public ITrackerUiContribution Create(ITrackerInstance instance)
            => new CloneTrackerContribution(instance);
    }

    private sealed class CloneTrackerContribution(ITrackerInstance instance) : ITrackerUiContribution
    {
        public string PluginId => instance.PluginId;
        public ITrackerInstance Instance => instance;
        public ViewModelBase? CreateSettingsPage(object configuration) => null;
        public ViewModelBase? CreateManagementPage(string instanceId) => null;
        public ITrackerEditorExtension? CreateEditorExtension(string instanceId)
            => new CloneTrackerExtension();
    }

    private sealed class CloneTrackerExtension : ViewModelBase, ITrackerEditorExtension
    {
        public TrackerKey Key => new("test.clone", "local");
        public string InstanceId => Key.InstanceId;
        ViewModelBase ITrackerEditorExtension.View => this;
        public bool IsLocked => false;
        public bool CanDelete => true;
        public bool OptionsLoaded { get; private set; }
        public bool OptionsLoadedWhenCloned { get; private set; }
        public string? Selection { get; set; }
        public int CloneCallCount { get; private set; }

        public void Load(WorkItem? item, object? binding = null)
            => OptionsLoaded = true;

        public bool Save(WorkItem item) => OptionsLoaded;

        public void CloneTo(ITrackerEditorExtension? target)
        {
            CloneCallCount++;
            if (target is not CloneTrackerExtension clone)
                return;
            clone.OptionsLoadedWhenCloned = clone.OptionsLoaded;
            clone.Selection = Selection;
        }

        public Task<TrackerOperationResult> UploadAsync(WorkItem item)
            => Task.FromResult(new TrackerOperationResult(true));
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

    private sealed class TestSqliteFactory : IDbFactory
    {
        private readonly Config _config = new() { FilePath = ":memory:" };

        public string Name => "SQLite";
        public bool Usable => true;
        public DbInterfaceBase Create() => new SQLiteDb(this);
        public Migration? GetMigration(uint version) => null;
        public object GetConfig() => _config;
    }

    private sealed class TestApplication : Application
    {
    }
}
