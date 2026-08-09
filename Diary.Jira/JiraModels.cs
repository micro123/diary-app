namespace Diary.Jira;

public sealed record JiraProject(
    string Key,
    string Name,
    string Description,
    bool Archived);

public sealed record JiraIssue(
    string Key,
    string Summary,
    string ProjectKey,
    string ProjectName,
    string Status,
    bool Closed);

public sealed record JiraWorklog(
    string Id,
    string IssueKey,
    long TimeSpentSeconds,
    DateTimeOffset Started);

public sealed record JiraApiResult<T>(
    bool Success,
    T? Value = default,
    string? Error = null,
    int? StatusCode = null)
{
    public static JiraApiResult<T> Ok(T value) => new(true, value);
    public static JiraApiResult<T> Fail(string error, int? statusCode = null) => new(false, default, error, statusCode);
}

public sealed record JiraIssueDisplay
{
    public required string Key { get; init; }
    public required string Summary { get; init; }
    public required string Project { get; init; }
    public required string Status { get; init; }
    public bool Disabled { get; init; }
    public bool Invalid { get; init; }
    public string DisplayTitle => Disabled || Invalid ? $"{Key} {Summary} [无效]" : $"{Key} {Summary}";
}

public sealed record JiraWorkTimeEntry
{
    public int WorkId { get; set; }
    public required string IssueKey { get; set; }
    public string? RemoteWorklogId { get; set; }
    public bool Uploaded => !string.IsNullOrWhiteSpace(RemoteWorklogId);
}
