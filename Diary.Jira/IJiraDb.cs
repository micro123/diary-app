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
    IDictionary<int, JiraWorkTimeEntry> GetWorkTimeEntriesByWorkItemIds(
        IReadOnlyCollection<int> workItemIds)
    {
        ArgumentNullException.ThrowIfNull(workItemIds);
        var result = new Dictionary<int, JiraWorkTimeEntry>();
        foreach (var id in workItemIds.Where(id => id > 0).Distinct())
        {
            var entry = WorkItemGetTimeEntry(new WorkItem { Id = id });
            if (entry is not null)
                result[id] = entry;
        }
        return result;
    }
    JiraWorkTimeEntry? CreateWorkTimeEntry(int workId, string issueKey);
    bool UpdateWorkTimeEntry(JiraWorkTimeEntry entry);
    bool WorkItemWasUploaded(WorkItem item);
    bool ClearData();
}
