using Diary.Core.Data.Base;
using Diary.Core.Data.RedMine;
using Diary.Database;

namespace Diary.Db.PostgreSQL;

public class PgDb : IDbInterface
{
    public object? Config => throw new NotImplementedException();

    public RedMineActivity AddRedMineActivity(int id, string title)
    {
        throw new NotImplementedException();
    }

    public RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project)
    {
        throw new NotImplementedException();
    }

    public RedMineProject AddRedMineProject(int id, string title)
    {
        throw new NotImplementedException();
    }

    public ICollection<WorkTag> AllWorkTags()
    {
        throw new NotImplementedException();
    }

    public void Close()
    {
        throw new NotImplementedException();
    }

    public bool Connect()
    {
        throw new NotImplementedException();
    }

    public WorkItem CreateWorkItem(string date, string comment, string note, double time)
    {
        throw new NotImplementedException();
    }

    public WorkTag CreateWorkTag(string name)
    {
        throw new NotImplementedException();
    }

    public WorkTimeEntry CreateWorkTimeEntry(WorkItem work, RedMineActivity activity, RedMineIssue issue)
    {
        throw new NotImplementedException();
    }

    public bool DeleteWorkItem(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public bool DeleteWorkTag(WorkTag tag)
    {
        throw new NotImplementedException();
    }

    public int GetDataVersion()
    {
        throw new NotImplementedException();
    }

    public ICollection<RedMineActivity> GetRedMineActivities()
    {
        throw new NotImplementedException();
    }

    public ICollection<RedMineIssue> GetRedMineIssues()
    {
        throw new NotImplementedException();
    }

    public ICollection<RedMineIssue> GetRedMineIssues(RedMineProject project)
    {
        throw new NotImplementedException();
    }

    public ICollection<RedMineProject> GetRedMineProjects()
    {
        throw new NotImplementedException();
    }

    public ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData)
    {
        throw new NotImplementedException();
    }

    public ICollection<WorkItemTag> GetWorkItemTags(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public bool Initialized()
    {
        throw new NotImplementedException();
    }

    public bool KeepAlive()
    {
        throw new NotImplementedException();
    }

    public bool UpdateTables(int targetVersion)
    {
        throw new NotImplementedException();
    }

    public bool UpdateWorkItem(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public bool UpdateWorkTag(WorkTag tag)
    {
        throw new NotImplementedException();
    }

    public bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry)
    {
        throw new NotImplementedException();
    }

    public bool WorkItemAddTag(WorkItem item, WorkTag tag)
    {
        throw new NotImplementedException();
    }

    public bool WorkItemCleanTags(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public bool WorkItemRemoveTag(WorkItem item, WorkTag tag)
    {
        throw new NotImplementedException();
    }
}
