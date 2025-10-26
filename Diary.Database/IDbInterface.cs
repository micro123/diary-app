using Diary.Core.Data.Base;
using Diary.Core.Data.RedMine;
namespace Diary.Database;

public interface IDbInterface
{
    // work tag
    WorkTag CreateWorkTag(string name);
    bool UpdateWorkTag(WorkTag tag);
    bool DeleteWorkTag(WorkTag tag);
    ICollection<WorkTag> GetWorkTags();
    
    // work item
    WorkItem CreateWorkItem(string date, string comment, string note, double time);
    bool UpdateWorkItem(WorkItem item);
    bool DeleteWorkItem(WorkItem item);
    ICollection<WorkItem> GetWorkItem(string beginData, string endData);
    
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
    bool WorkTimeEntrySetUploaded(WorkTimeEntry timeEntry);
}