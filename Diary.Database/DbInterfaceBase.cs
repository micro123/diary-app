using Diary.Core.Data.Base;
using Diary.Core.Data.RedMine;
namespace Diary.Database;

public abstract class DbInterfaceBase
{
    // driver config data
    public abstract object? Config { get; }

    // connect to db
    public abstract bool Connect();
    // check if initialized
    public abstract bool Initialized();
    // send keep alive heartbeat
    public abstract bool KeepAlive();
    // close connection
    public abstract void Close();

    // data version
    public abstract uint GetDataVersion();
    // migrate tables
    public abstract bool UpdateTables(uint targetVersion);

    // work tag
    public abstract WorkTag CreateWorkTag(string name);
    public abstract bool UpdateWorkTag(WorkTag tag);
    public abstract bool DeleteWorkTag(WorkTag tag);
    public abstract ICollection<WorkTag> AllWorkTags();

    // work item
    public abstract WorkItem CreateWorkItem(string date, string comment, string note, double time);
    public abstract bool UpdateWorkItem(WorkItem item);
    public abstract bool DeleteWorkItem(WorkItem item);
    public abstract ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData);

    // work item - work tag
    public abstract bool WorkItemAddTag(WorkItem item, WorkTag tag);
    public abstract bool WorkItemRemoveTag(WorkItem item, WorkTag tag);
    public abstract bool WorkItemCleanTags(WorkItem item);
    public abstract ICollection<WorkItemTag> GetWorkItemTags(WorkItem item);

    // redmine project
    public abstract RedMineActivity AddRedMineActivity(int id, string title);
    public abstract RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project);
    public abstract RedMineProject AddRedMineProject(int id, string title);

    public abstract ICollection<RedMineActivity> GetRedMineActivities();
    public abstract ICollection<RedMineIssue> GetRedMineIssues();
    public abstract ICollection<RedMineIssue> GetRedMineIssues(RedMineProject project);
    public abstract ICollection<RedMineProject> GetRedMineProjects();

    // time-entries
    public abstract WorkTimeEntry CreateWorkTimeEntry(WorkItem work, RedMineActivity activity, RedMineIssue issue);
    public abstract bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry);
}


