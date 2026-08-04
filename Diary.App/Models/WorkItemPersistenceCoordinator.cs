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
    IReadOnlyCollection<ITrackerEditorExtension> Extensions);

public sealed record WorkItemSaveResult(
    bool Success,
    bool Created,
    WorkItem? WorkItem = null,
    string? Error = null);

public interface IWorkItemPersistenceCoordinator
{
    WorkItemSaveResult Save(DbInterfaceBase db, WorkItemSaveRequest request);
}

[DiAutoRegister(singleton: true, serviceType: typeof(IWorkItemPersistenceCoordinator))]
public sealed class WorkItemPersistenceCoordinator : IWorkItemPersistenceCoordinator
{
    public WorkItemSaveResult Save(DbInterfaceBase db, WorkItemSaveRequest request)
    {
        if (!db.BeginTransaction())
            return new WorkItemSaveResult(false, false, Error: "无法开启数据库事务");

        var committed = false;
        try
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

            if (!string.IsNullOrWhiteSpace(request.Note))
                db.WorkUpdateNote(item, request.Note);
            else
                db.WorkDeleteNote(item);

            foreach (var extension in request.Extensions)
            {
                if (!extension.Save(item))
                    throw new InvalidOperationException($"保存 tracker 扩展失败: {extension.Key}");
            }

            if (created)
            {
                foreach (var tag in request.Tags)
                {
                    if (!db.WorkItemAddTag(item, tag))
                        throw new InvalidOperationException($"保存工作项标签失败: {tag.Id}");
                }
            }

            var commitSuccess = db.CommitTransaction();
            committed = true;
            if (!commitSuccess)
                return new WorkItemSaveResult(false, created, Error: "提交数据库事务失败");
            return new WorkItemSaveResult(true, created, item);
        }
        catch (Exception ex)
        {
            return new WorkItemSaveResult(false, false, Error: ex.Message);
        }
        finally
        {
            if (!committed)
                db.RollbackTransaction();
        }
    }
}
