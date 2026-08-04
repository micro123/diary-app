using Diary.Core.Data.Base;
using Diary.RedMine.Models;

namespace Diary.RedMine;

/// <summary>
/// RedMine 本地数据扩展契约。核心数据库只通过 DbInterfaceBase.GetExtension&lt;T&gt; 获取它。
/// </summary>
public interface IRedMineDb
{
    RedMineActivity AddRedMineActivity(int id, string title);
    RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project, bool closed = false);
    void UpdateRedMineIssueStatus(int id, bool closed);
    RedMineProject AddRedMineProject(int id, string title, string description);
    void UpdateRedMineProjectStatus(int id, bool closed);
    WorkTimeEntry? WorkItemGetTimeEntry(WorkItem item);
    IDictionary<int, WorkTimeEntry> GetWorkTimeEntriesByDate(string date);
    bool WorkItemWasUploaded(WorkItem item);

    ICollection<RedMineActivity> GetRedMineActivities();
    ICollection<RedMineIssueDisplay> GetRedMineIssues(RedMineProject? project);
    ICollection<RedMineProject> GetRedMineProjects();

    WorkTimeEntry? CreateWorkTimeEntry(int work, int activity, int issus);
    bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry);
}
