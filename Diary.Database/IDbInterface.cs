using Diary.Core.Data.Base;
using Diary.Core.Data.RedMine;
namespace Diary.Database;

public interface IDbInterface
{
    // name of the driver
    string Name { get; }
    
    // driver config data
    object? Config { get; }

    // connect to db
    bool Connect();
    // check if initialized
    bool Initialized();
    // send keep alive heartbeat
    bool KeepAlive();
    // close connection
    void Close();

    // data version
    int GetDataVersion();
    // migrate tables
    bool UpdateTables(int targetVersion);

    // work tag
    WorkTag CreateWorkTag(string name);
    bool UpdateWorkTag(WorkTag tag);
    bool DeleteWorkTag(WorkTag tag);
    ICollection<WorkTag> AllWorkTags();

    // work item
    WorkItem CreateWorkItem(string date, string comment, string note, double time);
    bool UpdateWorkItem(WorkItem item);
    bool DeleteWorkItem(WorkItem item);
    ICollection<WorkItem> GetWorkItemByDateRange(string beginData, string endData);

    // work item - work tag
    bool WorkItemAddTag(WorkItem item, WorkTag tag);
    bool WorkItemRemoveTag(WorkItem item, WorkTag tag);
    bool WorkItemCleanTags(WorkItem item);
    ICollection<WorkItemTag> GetWorkItemTags(WorkItem item);

    // redmine project
    RedMineActivity AddRedMineActivity(int id, string title);
    RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project);
    RedMineProject AddRedMineProject(int id, string title);

    ICollection<RedMineActivity> GetRedMineActivities();
    ICollection<RedMineIssue> GetRedMineIssues();
    ICollection<RedMineIssue> GetRedMineIssues(RedMineProject project);
    ICollection<RedMineProject> GetRedMineProjects();

    // time-entries
    WorkTimeEntry CreateWorkTimeEntry(WorkItem work, RedMineActivity activity, RedMineIssue issue);
    bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry);
}
