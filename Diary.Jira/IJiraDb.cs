using Diary.Core.Data.Base;

namespace Diary.Jira;

public interface IJiraDb
{
    string InstanceId { get; }
    uint SchemaVersion { get; }

    void UpsertProject(JiraProject project);
    void UpsertIssue(JiraIssue issue);
    ICollection<JiraIssueDisplay> GetIssues(bool openOnly = true);
    ICollection<JiraProject> GetProjects();
    JiraWorkTimeEntry? WorkItemGetTimeEntry(WorkItem item);
    IDictionary<int, JiraWorkTimeEntry> GetWorkTimeEntriesByDate(string date);
    JiraWorkTimeEntry? CreateWorkTimeEntry(int workId, string issueKey);
    bool UpdateWorkTimeEntry(JiraWorkTimeEntry entry);
    bool WorkItemWasUploaded(WorkItem item);
    bool ClearData();
}
