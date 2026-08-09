using System.Collections.ObjectModel;

namespace Diary.Jira.UI;

public sealed class JiraUiDataStore
{
    private readonly IJiraDb _database;
    public ObservableCollection<JiraIssueDisplay> Issues { get; } = new();

    public JiraUiDataStore(IJiraDb database) => _database = database;

    public void InitLoad()
    {
        Issues.Clear();
        foreach (var issue in _database.GetIssues()) Issues.Add(issue);
    }

    public async Task RefreshAsync(IJiraApi api, string? projectKey = null, string? query = null, CancellationToken cancellationToken = default)
    {
        var result = await api.SearchIssuesAsync(projectKey, query, cancellationToken: cancellationToken);
        if (!result.Success || result.Value is null)
            throw new InvalidOperationException(result.Error ?? "Jira Issue 查询失败。");
        foreach (var issue in result.Value)
        {
            _database.UpsertIssue(issue);
            if (Issues.All(item => item.Key != issue.Key)) Issues.Add(new JiraIssueDisplay
            {
                Key = issue.Key,
                Summary = issue.Summary,
                Project = issue.ProjectName,
                Status = issue.Status,
                Disabled = issue.Closed,
            });
        }
    }
}
