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
    public abstract WorkTag CreateWorkTag(string name, bool primary, int color);
    public abstract bool UpdateWorkTag(WorkTag tag);
    public abstract bool DeleteWorkTag(WorkTag tag);
    public abstract ICollection<WorkTag> AllWorkTags();

    // work item
    public abstract WorkItem CreateWorkItem(string date, string comment);
    public abstract bool UpdateWorkItem(WorkItem item);
    public abstract bool DeleteWorkItem(WorkItem item);
    public abstract ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData);
    public abstract ICollection<WorkItem> GetWorkItemByDate(string data);

    // work note
    public abstract void WorkUpdateNote(WorkItem work, string content);
    public abstract void WorkDeleteNote(WorkItem work);
    public abstract string? WorkGetNote(WorkItem work);
    
    // work item - work tag
    public abstract bool WorkItemAddTag(WorkItem item, WorkTag tag);
    public abstract bool WorkItemRemoveTag(WorkItem item, WorkTag tag);
    public abstract bool WorkItemCleanTags(WorkItem item);
    public abstract ICollection<WorkTag> GetWorkItemTags(WorkItem item);

    // redmine
    public abstract RedMineActivity AddRedMineActivity(int id, string title);
    public abstract RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project);
    public abstract void UpdateRedMineIssueStatus(int id, bool closed);
    public abstract RedMineProject AddRedMineProject(int id, string title, string description);
    public abstract void UpdateRedMineProjectStatus(int id, bool closed);

    public abstract ICollection<RedMineActivity> GetRedMineActivities();
    public abstract ICollection<RedMineIssue> GetRedMineIssues(RedMineProject? project);
    public abstract ICollection<RedMineProject> GetRedMineProjects();

    // time-entries
    public abstract WorkTimeEntry CreateWorkTimeEntry(WorkItem work, RedMineActivity activity, RedMineIssue issue);
    public abstract bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry);
}


