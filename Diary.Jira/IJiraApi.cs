namespace Diary.Jira;

public interface IJiraApi
{
    Task<JiraApiResult<IReadOnlyList<JiraProject>>> SearchProjectsAsync(
        string? query = null,
        int startAt = 0,
        int maxResults = 50,
        CancellationToken cancellationToken = default);

    Task<JiraApiResult<IReadOnlyList<JiraIssue>>> SearchIssuesAsync(
        string? projectKey = null,
        string? query = null,
        int startAt = 0,
        int maxResults = 50,
        CancellationToken cancellationToken = default);

    Task<JiraApiResult<JiraIssue>> GetIssueAsync(
        string issueKey,
        CancellationToken cancellationToken = default);

    Task<JiraApiResult<JiraWorklog>> AddWorklogAsync(
        string issueKey,
        DateOnly spentOn,
        double hours,
        string? comment,
        CancellationToken cancellationToken = default);
}
