using Diary.Core.Data.Base;
using Diary.Core.Data.RedMine;
using Diary.Database;

namespace Diary.Db.PostgreSQL;

public sealed class PgDb(IDbFactory factory) : DbInterfaceBase
{
    private readonly IDbFactory _factory = factory;
    public override object? Config { get; }
    public override bool Connect()
    {
        throw new NotImplementedException();
    }

    public override bool Initialized()
    {
        throw new NotImplementedException();
    }

    public override bool KeepAlive()
    {
        throw new NotImplementedException();
    }

    public override void Close()
    {
        throw new NotImplementedException();
    }

    public override uint GetDataVersion()
    {
        throw new NotImplementedException();
    }

    public override bool UpdateTables(uint targetVersion)
    {
        throw new NotImplementedException();
    }

    public override WorkTag CreateWorkTag(string name)
    {
        throw new NotImplementedException();
    }

    public override bool UpdateWorkTag(WorkTag tag)
    {
        throw new NotImplementedException();
    }

    public override bool DeleteWorkTag(WorkTag tag)
    {
        throw new NotImplementedException();
    }

    public override ICollection<WorkTag> AllWorkTags()
    {
        throw new NotImplementedException();
    }

    public override WorkItem CreateWorkItem(string date, string comment, string note, double time)
    {
        throw new NotImplementedException();
    }

    public override bool UpdateWorkItem(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public override bool DeleteWorkItem(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public override ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData)
    {
        throw new NotImplementedException();
    }

    public override bool WorkItemAddTag(WorkItem item, WorkTag tag)
    {
        throw new NotImplementedException();
    }

    public override bool WorkItemRemoveTag(WorkItem item, WorkTag tag)
    {
        throw new NotImplementedException();
    }

    public override bool WorkItemCleanTags(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public override ICollection<WorkItemTag> GetWorkItemTags(WorkItem item)
    {
        throw new NotImplementedException();
    }

    public override RedMineActivity AddRedMineActivity(int id, string title)
    {
        throw new NotImplementedException();
    }

    public override RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project)
    {
        throw new NotImplementedException();
    }

    public override RedMineProject AddRedMineProject(int id, string title)
    {
        throw new NotImplementedException();
    }

    public override ICollection<RedMineActivity> GetRedMineActivities()
    {
        throw new NotImplementedException();
    }

    public override ICollection<RedMineIssue> GetRedMineIssues()
    {
        throw new NotImplementedException();
    }

    public override ICollection<RedMineIssue> GetRedMineIssues(RedMineProject project)
    {
        throw new NotImplementedException();
    }

    public override ICollection<RedMineProject> GetRedMineProjects()
    {
        throw new NotImplementedException();
    }

    public override WorkTimeEntry CreateWorkTimeEntry(WorkItem work, RedMineActivity activity, RedMineIssue issue)
    {
        throw new NotImplementedException();
    }

    public override bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry)
    {
        throw new NotImplementedException();
    }
}
