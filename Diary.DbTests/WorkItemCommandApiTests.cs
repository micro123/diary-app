using Diary.App.Models;
using Diary.App.Services;
using Diary.Core.Data.Base;
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
        var api = CreateApi(db);
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
        var api = CreateApi(db);

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

    private static WorkItemCommandApi CreateApi(Diary.Database.DbInterfaceBase db) => new(
        () => db,
        new WorkItemPersistenceCoordinator(),
        new WorkItemCommandIdempotencyStore());

    private sealed class FailingPersistence : IWorkItemPersistenceCoordinator
    {
        public WorkItemSaveResult Save(Diary.Database.DbInterfaceBase db, WorkItemSaveRequest request) =>
            new(false, false, Error: "模拟事务失败");
    }
}
