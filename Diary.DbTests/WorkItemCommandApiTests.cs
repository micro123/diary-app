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
