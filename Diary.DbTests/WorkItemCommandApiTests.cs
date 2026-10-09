using Diary.App.Models;
using Diary.App.Services;
using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.ScriptBase;
using Diary.ScriptHost;

namespace Diary.DbTests;

[TestClass]
public sealed class WorkItemCommandApiTests
{
    [TestMethod]
    public async Task PreviewConfirmAndDuplicateCreateExactlyOneItemWithFields()
    {
        using var db = TestDb.Create();
        var tag = db.CreateWorkTag("项目", true, 0x123456);
        var definition = new TagExtraFieldDefinition
        {
            FieldId = Guid.NewGuid().ToString("D"),
            FieldKey = "project.code",
            TagId = tag.Id,
            Label = "项目代码",
            Type = TagExtraFieldType.Text,
        };
        Assert.IsTrue(db.CreateTagExtraFieldDefinition(definition));
        var automation = new RecordingAutomationPublisher();
        var api = CreateApi(db, automation);
        var command = new WorkItemCreateCommand(
            "2026-10-08",
            "Agent 创建事项",
            1.5,
            (int)WorkPriorities.P2,
            [tag.Id],
            [new WorkItemExtraFieldCommand(definition.FieldId, "D-42")],
            "用户确认写入的备注",
            "agent-create-1");

        var preview = await api.PreviewCreateAsync(command);
        Assert.IsTrue(preview.Succeeded);
        Assert.IsNotNull(preview.Command);
        Assert.IsFalse(string.IsNullOrWhiteSpace(preview.PreviewVersion));

        var first = await api.CreateAsync(preview.Command);
        var duplicate = await api.CreateAsync(preview.Command);

        Assert.IsTrue(first.Succeeded);
        Assert.IsFalse(first.Duplicate);
        Assert.IsTrue(duplicate.Succeeded);
        Assert.IsTrue(duplicate.Duplicate);
        Assert.AreEqual(first.WorkItemId, duplicate.WorkItemId);
        var item = db.GetWorkItemByDate("2026-10-08").Single();
        Assert.AreEqual("Agent 创建事项", item.Comment);
        Assert.AreEqual(1.5, item.Time);
        Assert.AreEqual("用户确认写入的备注", db.WorkGetNote(item));
        Assert.AreEqual(tag.Id, db.GetWorkItemTags(item).Single().Id);
        Assert.AreEqual("D-42", db.GetWorkItemExtraFields(item).Single().Value);
        CollectionAssert.AreEqual(
            new[] { ScriptAutomationTriggerKind.WorkItemCreated, ScriptAutomationTriggerKind.TagAdded },
            automation.Events.Select(item => item.Trigger).ToArray());
        Assert.AreEqual(item.Id, automation.Events[0].WorkItemId);
        Assert.AreEqual(tag.Id, automation.Events[1].TagId);
    }

    [TestMethod]
    public async Task CreatePersistsPreparedTrackerDefaultsWithoutRemoteUpload()
    {
        using var db = TestDb.Create();
        var tag = db.CreateWorkTag("Redmine 项目", true, 0);
        var tagAutomation = new RecordingTagAutomationService();
        var api = CreateApi(db, tagAutomation: tagAutomation);
        var command = new WorkItemCreateCommand(
            "2026-10-09",
            "Agent 创建并应用 Redmine 默认值",
            1,
            (int)WorkPriorities.P2,
            [tag.Id],
            [],
            null,
            "agent-create-redmine-defaults");

        var preview = await api.PreviewCreateAsync(command);
        var result = await api.CreateAsync(preview.Command!);

        Assert.IsTrue(result.Succeeded, result.ErrorMessage);
        Assert.AreEqual(1, tagAutomation.PrepareCount);
        Assert.AreEqual(TagAddSource.Agent, tagAutomation.Source);
        CollectionAssert.AreEqual(new[] { tag.Id }, tagAutomation.AddedTagIds);
        Assert.AreEqual(1, tagAutomation.Extension.SaveCount);
        Assert.AreEqual(result.WorkItemId, tagAutomation.Extension.SavedItemId);
        Assert.AreEqual(0, tagAutomation.Extension.UploadCount);
        Assert.IsTrue(tagAutomation.Extension.Disposed);
    }

    [TestMethod]
    public async Task TagAutomationFailureDoesNotCreateWorkItem()
    {
        using var db = TestDb.Create();
        var tag = db.CreateWorkTag("失败规则", true, 0);
        var api = CreateApi(db, tagAutomation: new FailingTagAutomationService());
        var command = new WorkItemCreateCommand(
            "2026-10-09",
            "不应写入",
            1,
            (int)WorkPriorities.P2,
            [tag.Id],
            [],
            null,
            "agent-create-tag-automation-failure");

        var preview = await api.PreviewCreateAsync(command);
        var result = await api.CreateAsync(preview.Command!);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("tag_automation_failed", result.ErrorCode);
        Assert.AreEqual(0, db.GetWorkItemByDate("2026-10-09").Count);
    }

    [TestMethod]
    public async Task ChangedTagInvalidatesPreviewAndDoesNotWrite()
    {
        using var db = TestDb.Create();
        var tag = db.CreateWorkTag("即将停用", true, 0);
        var api = CreateApi(db);
        var preview = await api.PreviewCreateAsync(new WorkItemCreateCommand(
            "2026-10-08",
            "过期预览",
            1,
            0,
            [tag.Id],
            [],
            null,
            "agent-stale-1"));
        Assert.IsTrue(preview.Succeeded);
        tag.Disabled = true;
        Assert.IsTrue(db.UpdateWorkTag(tag));

        var result = await api.CreateAsync(preview.Command!);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("tag_changed", result.ErrorCode);
        Assert.AreEqual(0, db.GetWorkItemByDate("2026-10-08").Count);
    }

    [TestMethod]
    public async Task PreviewVersionMismatchRequiresFreshConfirmation()
    {
        using var db = TestDb.Create();
        var api = CreateApi(db);
        var preview = await api.PreviewCreateAsync(new WorkItemCreateCommand(
            "2026-10-08",
            "预览版本",
            1,
            0,
            [],
            [],
            null,
            "agent-version-1"));

        var result = await api.CreateAsync(preview.Command! with { PreviewVersion = new string('0', 64) });

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("preview_stale", result.ErrorCode);
        Assert.AreEqual(0, db.GetWorkItemByDate("2026-10-08").Count);
    }

    [TestMethod]
    public async Task PersistenceFailureIsNotStoredAsSuccessfulIdempotencyResult()
    {
        using var db = TestDb.Create();
        var store = new WorkItemCommandIdempotencyStore();
        var failedApi = new WorkItemCommandApi(() => db, new FailingPersistence(), store);
        var command = new WorkItemCreateCommand(
            "2026-10-08",
            "失败后可重试",
            1,
            0,
            [],
            [],
            null,
            "agent-retry-1");
        var preview = await failedApi.PreviewCreateAsync(command);
        var failure = await failedApi.CreateAsync(preview.Command!);
        Assert.IsFalse(failure.Succeeded);

        var successfulApi = new WorkItemCommandApi(
            () => db,
            new WorkItemPersistenceCoordinator(),
            store);
        var freshPreview = await successfulApi.PreviewCreateAsync(command);
        var success = await successfulApi.CreateAsync(freshPreview.Command!);

        Assert.IsTrue(success.Succeeded);
        Assert.IsFalse(success.Duplicate);
        Assert.AreEqual(1, db.GetWorkItemByDate("2026-10-08").Count);
    }

    [TestMethod]
    public async Task UpdatePreservesLocalNoteAndAppliesConfirmedFieldsAndTagsOnce()
    {
        using var db = TestDb.Create();
        var item = db.CreateWorkItem("2026-10-08", "旧标题");
        item.Time = 1;
        item.Priority = WorkPriorities.P1;
        Assert.IsTrue(db.UpdateWorkItem(item));
        db.WorkUpdateNote(item, "LOCAL_NOTE_MUST_BE_PRESERVED");
        var oldTag = db.CreateWorkTag("旧标签", true, 0);
        var newTag = db.CreateWorkTag("新标签", false, 0);
        Assert.IsTrue(db.WorkItemAddTag(item, oldTag));
        var automation = new RecordingAutomationPublisher();
        var api = CreateApi(db, automation);

        var preview = await api.PreviewUpdateAsync(new WorkItemUpdateCommand(
            item.Id,
            "2026-10-09",
            "新标题",
            2.5,
            3,
            [newTag.Id],
            "agent-update-1"));
        var first = await api.UpdateAsync(preview.Command!);
        var duplicate = await api.UpdateAsync(preview.Command!);

        Assert.IsTrue(first.Succeeded, first.ErrorMessage);
        Assert.IsFalse(first.Duplicate);
        Assert.IsTrue(duplicate.Succeeded);
        Assert.IsTrue(duplicate.Duplicate);
        var updated = db.QueryWorkItems(new WorkItemQuery { WorkItemId = item.Id }).Single();
        Assert.AreEqual("2026-10-09", updated.CreateDate);
        Assert.AreEqual("新标题", updated.Comment);
        Assert.AreEqual(2.5, updated.Time);
        Assert.AreEqual(WorkPriorities.P3, updated.Priority);
        Assert.AreEqual("LOCAL_NOTE_MUST_BE_PRESERVED", db.WorkGetNote(updated));
        CollectionAssert.AreEqual(new[] { newTag.Id }, db.GetWorkItemTags(updated).Select(tag => tag.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { ScriptAutomationTriggerKind.WorkItemSaved, ScriptAutomationTriggerKind.TagAdded },
            automation.Events.Select(item => item.Trigger).ToArray());
        Assert.AreEqual(newTag.Id, automation.Events[1].TagId);
    }

    [TestMethod]
    public async Task ChangedWorkItemInvalidatesUpdatePreview()
    {
        using var db = TestDb.Create();
        var item = db.CreateWorkItem("2026-10-08", "原始标题");
        item.Time = 1;
        Assert.IsTrue(db.UpdateWorkItem(item));
        var api = CreateApi(db);
        var preview = await api.PreviewUpdateAsync(new WorkItemUpdateCommand(
            item.Id,
            null,
            "Agent 标题",
            null,
            null,
            null,
            "agent-update-stale"));
        item.Comment = "用户已修改";
        Assert.IsTrue(db.UpdateWorkItem(item));

        var result = await api.UpdateAsync(preview.Command!);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("preview_stale", result.ErrorCode);
        Assert.AreEqual("用户已修改", db.QueryWorkItems(new WorkItemQuery { WorkItemId = item.Id }).Single().Comment);
    }

    [TestMethod]
    public async Task UpdateAppliesExtraFieldPatchAndPreservesOtherValues()
    {
        using var db = TestDb.Create();
        var tag = db.CreateWorkTag("项目", true, 0);
        var codeField = CreateField(db, tag.Id, "project.code", "项目代码");
        var ownerField = CreateField(db, tag.Id, "project.owner", "负责人");
        var item = db.CreateWorkItem("2026-10-08", "附加字段事项");
        item.Time = 1;
        Assert.IsTrue(db.UpdateWorkItem(item));
        Assert.IsTrue(db.WorkItemAddTag(item, tag));
        Assert.IsTrue(db.SaveWorkItemExtraFieldValues(item.Id, [
            new WorkItemExtraFieldValue { WorkItemId = item.Id, FieldId = codeField.FieldId, Value = "OLD" },
            new WorkItemExtraFieldValue { WorkItemId = item.Id, FieldId = ownerField.FieldId, Value = "Alice" },
        ]));
        db.WorkUpdateNote(item, "LOCAL_NOTE");
        var api = CreateApi(db);

        var preview = await api.PreviewUpdateAsync(new WorkItemUpdateCommand(
            item.Id,
            null,
            null,
            null,
            null,
            null,
            "agent-extra-field-update",
            [new WorkItemExtraFieldCommand(codeField.FieldId, "NEW")]));
        var result = await api.UpdateAsync(preview.Command!);

        Assert.IsTrue(result.Succeeded, result.ErrorMessage);
        var values = db.GetWorkItemExtraFields(item)
            .Where(field => !string.IsNullOrWhiteSpace(field.Value))
            .ToDictionary(field => field.FieldId, field => field.Value);
        Assert.AreEqual("NEW", values[codeField.FieldId]);
        Assert.AreEqual("Alice", values[ownerField.FieldId]);
        Assert.AreEqual("LOCAL_NOTE", db.WorkGetNote(item));
    }

    [TestMethod]
    public async Task BatchUpdateWritesAllItemsAtomicallyAndIsIdempotent()
    {
        using var db = TestDb.Create();
        var tag = db.CreateWorkTag("项目", true, 0);
        var field = CreateField(db, tag.Id, "project.code", "项目代码");
        var first = db.CreateWorkItem("2026-10-08", "事项 A");
        first.Time = 1;
        Assert.IsTrue(db.UpdateWorkItem(first));
        Assert.IsTrue(db.WorkItemAddTag(first, tag));
        var second = db.CreateWorkItem("2026-10-08", "事项 B");
        second.Time = 1;
        Assert.IsTrue(db.UpdateWorkItem(second));
        Assert.IsTrue(db.WorkItemAddTag(second, tag));
        var automation = new RecordingAutomationPublisher();
        var api = CreateApi(db, automation);
        var command = new WorkItemBatchUpdateCommand([
            new WorkItemUpdateCommand(first.Id, null, "事项 A+", null, null, null, string.Empty,
                [new WorkItemExtraFieldCommand(field.FieldId, "A")]),
            new WorkItemUpdateCommand(second.Id, null, "事项 B+", 2, null, null, string.Empty,
                [new WorkItemExtraFieldCommand(field.FieldId, "B")]),
        ], "batch-update-1");

        var preview = await api.PreviewBatchUpdateAsync(command);
        var result = await api.BatchUpdateAsync(preview.Command!);
        var duplicate = await api.BatchUpdateAsync(new WorkItemBatchUpdateCommand([
            new WorkItemUpdateCommand(999999, null, "不应执行", null, null, null, string.Empty),
        ], "batch-update-1", "不同预览版本"));

        Assert.IsTrue(result.Succeeded, result.ErrorMessage);
        Assert.IsFalse(result.Duplicate);
        Assert.IsTrue(duplicate.Succeeded);
        Assert.IsTrue(duplicate.Duplicate);
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, result.WorkItemIds.ToArray());
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, duplicate.WorkItemIds.ToArray());
        var updatedFirst = db.QueryWorkItems(new WorkItemQuery { WorkItemId = first.Id }).Single();
        var updatedSecond = db.QueryWorkItems(new WorkItemQuery { WorkItemId = second.Id }).Single();
        Assert.AreEqual("事项 A+", updatedFirst.Comment);
        Assert.AreEqual("事项 B+", updatedSecond.Comment);
        Assert.AreEqual(2, updatedSecond.Time);
        Assert.AreEqual("A", db.GetWorkItemExtraFields(updatedFirst).Single(value => value.FieldId == field.FieldId).Value);
        Assert.AreEqual("B", db.GetWorkItemExtraFields(updatedSecond).Single(value => value.FieldId == field.FieldId).Value);
        Assert.AreEqual(2, automation.Events.Count);
        Assert.IsTrue(automation.Events.All(item => item.Trigger == ScriptAutomationTriggerKind.WorkItemSaved));
    }

    [TestMethod]
    public async Task BatchTagAutomationFailureDoesNotWriteAnyItem()
    {
        using var db = TestDb.Create();
        var tag = db.CreateWorkTag("批量新增标签", true, 0);
        var first = db.CreateWorkItem("2026-10-08", "事项 A");
        first.Time = 1;
        Assert.IsTrue(db.UpdateWorkItem(first));
        var second = db.CreateWorkItem("2026-10-08", "事项 B");
        second.Time = 1;
        Assert.IsTrue(db.UpdateWorkItem(second));
        var api = CreateApi(db, tagAutomation: new FailOnSecondTagAutomationService());
        var command = new WorkItemBatchUpdateCommand(
            [
                new WorkItemUpdateCommand(first.Id, null, "事项 A+", null, null, [tag.Id], "item-a"),
                new WorkItemUpdateCommand(second.Id, null, "事项 B+", null, null, [tag.Id], "item-b"),
            ],
            "batch-tag-automation-failure");

        var preview = await api.PreviewBatchUpdateAsync(command);
        var result = await api.BatchUpdateAsync(preview.Command!);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("tag_automation_failed", result.ErrorCode);
        Assert.AreEqual("事项 A", db.QueryWorkItems(new WorkItemQuery { WorkItemId = first.Id }).Single().Comment);
        Assert.AreEqual("事项 B", db.QueryWorkItems(new WorkItemQuery { WorkItemId = second.Id }).Single().Comment);
        Assert.AreEqual(0, db.GetWorkItemTags(first).Count);
        Assert.AreEqual(0, db.GetWorkItemTags(second).Count);
    }

    private static TagExtraFieldDefinition CreateField(
        Diary.Database.DbInterfaceBase db,
        int tagId,
        string key,
        string label)
    {
        var field = new TagExtraFieldDefinition
        {
            FieldId = Guid.NewGuid().ToString("D"),
            FieldKey = key,
            TagId = tagId,
            Label = label,
            Type = TagExtraFieldType.Text,
        };
        Assert.IsTrue(db.CreateTagExtraFieldDefinition(field));
        return field;
    }

    private static WorkItemCommandApi CreateApi(
        Diary.Database.DbInterfaceBase db,
        IWorkItemAutomationPublisher? automationPublisher = null,
        IWorkItemTagAutomationService? tagAutomation = null) => new(
        () => db,
        new WorkItemPersistenceCoordinator(),
        new WorkItemCommandIdempotencyStore(),
        automationPublisher: automationPublisher,
        tagAutomation: tagAutomation);

    private sealed class RecordingAutomationPublisher : IWorkItemAutomationPublisher
    {
        public List<WorkItemAutomationEvent> Events { get; } = [];

        public void Publish(WorkItemAutomationEvent automationEvent) => Events.Add(automationEvent);
    }

    private sealed class FailingPersistence : IWorkItemPersistenceCoordinator
    {
        public WorkItemSaveResult Save(Diary.Database.DbInterfaceBase db, WorkItemSaveRequest request) =>
            new(false, false, Error: "模拟事务失败");
    }

    private sealed class RecordingTagAutomationService : IWorkItemTagAutomationService
    {
        public int PrepareCount { get; private set; }
        public TagAddSource Source { get; private set; }
        public int[] AddedTagIds { get; private set; } = [];
        public RecordingTrackerExtension Extension { get; } = new();

        public WorkItemTagAutomationPreparation Prepare(
            WorkItem? existing,
            IReadOnlyCollection<WorkTag> addedTags,
            TagAddSource source)
        {
            PrepareCount++;
            Source = source;
            AddedTagIds = addedTags.Select(tag => tag.Id).ToArray();
            return WorkItemTagAutomationPreparation.Success(
                [Extension],
                new TagAutomationResult([]));
        }
    }

    private sealed class FailingTagAutomationService : IWorkItemTagAutomationService
    {
        public WorkItemTagAutomationPreparation Prepare(
            WorkItem? existing,
            IReadOnlyCollection<WorkTag> addedTags,
            TagAddSource source) => WorkItemTagAutomationPreparation.Failure("模拟标签自动化失败");
    }

    private sealed class FailOnSecondTagAutomationService : IWorkItemTagAutomationService
    {
        private int _count;

        public WorkItemTagAutomationPreparation Prepare(
            WorkItem? existing,
            IReadOnlyCollection<WorkTag> addedTags,
            TagAddSource source)
        {
            _count++;
            return _count == 2
                ? WorkItemTagAutomationPreparation.Failure("第二项标签自动化失败")
                : WorkItemTagAutomationPreparation.Success([new RecordingTrackerExtension()], new TagAutomationResult([]));
        }
    }

    private sealed class RecordingTrackerExtension : ITrackerEditorExtension, IDisposable
    {
        public TrackerKey Key => new("redmine", "company");
        public string InstanceId => Key.InstanceId;
        public ViewModelBase View { get; } = new();
        public bool IsLocked => false;
        public bool CanDelete => true;
        public int SaveCount { get; private set; }
        public int? SavedItemId { get; private set; }
        public int UploadCount { get; private set; }
        public bool Disposed { get; private set; }

        public void Load(WorkItem? item, object? binding = null) { }

        public bool Save(WorkItem item)
        {
            SaveCount++;
            SavedItemId = item.Id;
            return true;
        }

        public void CloneTo(ITrackerEditorExtension? target) { }

        public Task<TrackerOperationResult> UploadAsync(WorkItem item)
        {
            UploadCount++;
            return Task.FromResult(new TrackerOperationResult(true));
        }

        public void Dispose() => Disposed = true;
    }
}
