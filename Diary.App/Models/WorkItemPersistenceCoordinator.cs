using Diary.Core.Data.Base;
using Diary.Database;
using Diary.PluginUI;
using Diary.Utils;

namespace Diary.App.Models;

public sealed record WorkItemSaveRequest(
    WorkItem? Existing,
    string Date,
    string Comment,
    string Note,
    double Time,
    WorkPriorities Priority,
    IReadOnlyCollection<WorkTag> Tags,
    IReadOnlyCollection<WorkItemExtraFieldValue> ExtraFieldValues,
    IReadOnlyCollection<ITrackerEditorExtension> Extensions,
    bool PreserveNote = false);

public sealed record WorkItemSaveResult(
    bool Success,
    bool Created,
    WorkItem? WorkItem = null,
    string? Error = null);

public sealed record WorkItemBatchSaveResult(
    bool Success,
    IReadOnlyList<WorkItemSaveResult> Results,
    string? Error = null);

public interface IWorkItemPersistenceCoordinator
{
    WorkItemSaveResult Save(DbInterfaceBase db, WorkItemSaveRequest request);

    WorkItemBatchSaveResult SaveBatch(DbInterfaceBase db, IReadOnlyList<WorkItemSaveRequest> requests) =>
        new(false, [], "当前持久化协调器不支持批量保存");
}

[DiAutoRegister(singleton: true, serviceType: typeof(IWorkItemPersistenceCoordinator))]
public sealed class WorkItemPersistenceCoordinator : IWorkItemPersistenceCoordinator
{
    public WorkItemSaveResult Save(DbInterfaceBase db, WorkItemSaveRequest request)
    {
        var batch = SaveBatch(db, [request]);
        return batch.Success
            ? batch.Results[0]
            : new WorkItemSaveResult(false, false, Error: batch.Error);
    }

    public WorkItemBatchSaveResult SaveBatch(DbInterfaceBase db, IReadOnlyList<WorkItemSaveRequest> requests)
    {
        if (requests.Count == 0)
            return new WorkItemBatchSaveResult(false, [], "批量保存至少需要一个工作项");
        if (requests.Any(request => request.Existing?.IsReadOnly == true))
            return new WorkItemBatchSaveResult(false, [], "迁移导入的工作项是只读统计记录，不可编辑");

        if (!db.BeginTransaction())
            return new WorkItemBatchSaveResult(false, [], "无法开启数据库事务");

        var committed = false;
        try
        {
            var results = requests.Select(request => SaveWithinTransaction(db, request)).ToArray();

            var commitSuccess = db.CommitTransaction();
            committed = commitSuccess;
            if (!commitSuccess)
                return new WorkItemBatchSaveResult(false, [], "提交数据库事务失败");
            return new WorkItemBatchSaveResult(true, results);
        }
        catch (Exception ex)
        {
            return new WorkItemBatchSaveResult(false, [], ex.Message);
        }
        finally
        {
            if (!committed)
                db.RollbackTransaction();
        }
    }

    private static WorkItemSaveResult SaveWithinTransaction(DbInterfaceBase db, WorkItemSaveRequest request)
    {
        var created = request.Existing is null;
        var item = created
            ? db.CreateWorkItem(request.Date, request.Comment)
            : request.Existing! with
            {
                CreateDate = request.Date,
                Comment = request.Comment,
            };

        if (item.Id <= 0)
            throw new InvalidOperationException("创建工作项失败");

        item.CreateDate = request.Date;
        item.Comment = request.Comment;
        item.Time = request.Time;
        item.Priority = request.Priority;

        if (!db.UpdateWorkItem(item))
            throw new InvalidOperationException("更新工作项失败");

        if (!request.PreserveNote)
        {
            if (!string.IsNullOrWhiteSpace(request.Note))
                db.WorkUpdateNote(item, request.Note);
            else
                db.WorkDeleteNote(item);
        }

        foreach (var extension in request.Extensions)
        {
            if (!extension.Save(item))
                throw new InvalidOperationException($"保存 tracker 扩展失败: {extension.Key}");
        }

        var existingTags = created
            ? new Dictionary<int, WorkTag>()
            : db.GetWorkItemTags(item).ToDictionary(tag => tag.Id);
        var desiredTags = request.Tags.ToDictionary(tag => tag.Id);
        foreach (var tag in existingTags.Values.Where(tag => !desiredTags.ContainsKey(tag.Id)))
        {
            if (!db.WorkItemRemoveTag(item, tag))
                throw new InvalidOperationException($"移除工作项标签失败: {tag.Id}");
        }
        foreach (var tag in desiredTags.Values.Where(tag => !existingTags.ContainsKey(tag.Id)))
        {
            if (!db.WorkItemAddTag(item, tag))
                throw new InvalidOperationException($"保存工作项标签失败: {tag.Id}");
        }

        if (!db.SaveWorkItemExtraFieldValues(item.Id, request.ExtraFieldValues))
            throw new InvalidOperationException("保存附加字段失败");
        return new WorkItemSaveResult(true, created, item);
    }
}
