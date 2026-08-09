using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diary.Jira;

public sealed class JiraApi : IJiraApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly JiraConfig _configuration;
    private readonly HttpClient _httpClient;

    public JiraApi(JiraConfig? configuration = null, HttpClient? httpClient = null)
    {
        _configuration = configuration
            ?? JiraConfigurationStore.Current.Instances.FirstOrDefault()
            ?? new JiraInstanceSettings();
        _httpClient = httpClient ?? new HttpClient();
    }

    public Task<JiraApiResult<IReadOnlyList<JiraProject>>> SearchProjectsAsync(
        string? query = null, int startAt = 0, int maxResults = 50, CancellationToken cancellationToken = default)
    {
        var path = $"/rest/api/3/project/search?startAt={Math.Max(0, startAt)}&maxResults={Math.Clamp(maxResults, 1, 100)}";
        if (!string.IsNullOrWhiteSpace(query))
            path += $"&query={Uri.EscapeDataString(query.Trim())}";
        return SendAsync<JiraProjectPage, IReadOnlyList<JiraProject>>(HttpMethod.Get, path, null, page =>
            page.Values.Select(project => new JiraProject(
                project.Key ?? string.Empty, project.Name ?? string.Empty,
                project.Description ?? string.Empty, project.Archived)).ToArray(), cancellationToken);
    }

    public Task<JiraApiResult<IReadOnlyList<JiraIssue>>> SearchIssuesAsync(
        string? projectKey = null, string? query = null, int startAt = 0, int maxResults = 50, CancellationToken cancellationToken = default)
    {
        var clauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectKey))
            clauses.Add($"project = \"{EscapeJql(projectKey)}\"");
        if (!string.IsNullOrWhiteSpace(query))
            clauses.Add($"text ~ \"{EscapeJql(query)}\"");
        var jql = clauses.Count == 0 ? "ORDER BY updated DESC" : string.Join(" AND ", clauses) + " ORDER BY updated DESC";
        var path = $"/rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&startAt={Math.Max(0, startAt)}&maxResults={Math.Clamp(maxResults, 1, 100)}&fields=summary,project,status";
        return SendAsync<JiraIssuePage, IReadOnlyList<JiraIssue>>(HttpMethod.Get, path, null, page =>
            page.Issues.Select(MapIssue).ToArray(), cancellationToken);
    }

    public Task<JiraApiResult<JiraIssue>> GetIssueAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
            return Task.FromResult(JiraApiResult<JiraIssue>.Fail("Jira Issue Key 不能为空。"));
        var path = $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey.Trim())}?fields=summary,project,status";
        return SendAsync<JiraIssuePayload, JiraIssue>(HttpMethod.Get, path, null, MapIssue, cancellationToken);
    }

    public Task<JiraApiResult<JiraWorklog>> AddWorklogAsync(
        string issueKey, DateOnly spentOn, double hours, string? comment, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issueKey))
            return Task.FromResult(JiraApiResult<JiraWorklog>.Fail("Jira Issue Key 不能为空。"));
        if (hours <= 0 || double.IsNaN(hours) || double.IsInfinity(hours))
            return Task.FromResult(JiraApiResult<JiraWorklog>.Fail("工时必须大于 0。"));

        var body = new JiraWorklogRequest(
            checked((long)Math.Round(hours * 3600, MidpointRounding.AwayFromZero)),
            spentOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff+0000"),
            string.IsNullOrWhiteSpace(comment) ? null : JiraDocument.FromText(comment));
        var path = $"/rest/api/3/issue/{Uri.EscapeDataString(issueKey.Trim())}/worklog";
        return SendAsync<JiraWorklogPayload, JiraWorklog>(HttpMethod.Post, path, body, payload =>
            new JiraWorklog(payload.Id ?? string.Empty, issueKey.Trim(), payload.TimeSpentSeconds, ParseStarted(payload.Started)), cancellationToken);
    }

    private async Task<JiraApiResult<TResult>> SendAsync<TPayload, TResult>(
        HttpMethod method, string path, object? body, Func<TPayload, TResult> map, CancellationToken cancellationToken)
    {
        if (!_configuration.Valid())
            return JiraApiResult<TResult>.Fail("Jira 服务地址、认证信息或 Token 配置无效。");

        using var request = new HttpRequestMessage(method, new Uri(new Uri(_configuration.ServerUrl.TrimEnd('/') + "/"), path.TrimStart('/')));
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        if (_configuration.UseBearerToken)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _configuration.ApiToken);
        else
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_configuration.UserName}:{_configuration.ApiToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return JiraApiResult<TResult>.Fail(FormatError(response.StatusCode, payload), (int)response.StatusCode);
            var value = JsonSerializer.Deserialize<TPayload>(payload, JsonOptions);
            return value is null
                ? JiraApiResult<TResult>.Fail("Jira 返回了空响应。", (int)response.StatusCode)
                : JiraApiResult<TResult>.Ok(map(value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return JiraApiResult<TResult>.Fail("Jira 请求已取消。");
        }
        catch (HttpRequestException exception)
        {
            return JiraApiResult<TResult>.Fail($"Jira 请求失败：{exception.Message}");
        }
        catch (JsonException exception)
        {
            return JiraApiResult<TResult>.Fail($"Jira 返回数据格式无效：{exception.Message}");
        }
    }

    private static JiraIssue MapIssue(JiraIssuePayload issue)
    {
        var project = issue.Fields?.Project;
        var status = issue.Fields?.Status;
        return new JiraIssue(
            issue.Key ?? string.Empty,
            issue.Fields?.Summary ?? string.Empty,
            project?.Key ?? string.Empty,
            project?.Name ?? string.Empty,
            status?.Name ?? string.Empty,
            status?.CategoryKey is "done" or "closed");
    }

    private static string EscapeJql(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static DateTimeOffset ParseStarted(string value)
    {
        if (value.Length >= 5 && value[^5] is '+' or '-' && value[^3] != ':')
            value = value[..^2] + ":" + value[^2..];
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    private static string FormatError(HttpStatusCode statusCode, string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return $"Jira 请求失败，HTTP {(int)statusCode}。";
        return $"Jira 请求失败，HTTP {(int)statusCode}：{payload[..Math.Min(payload.Length, 500)]}";
    }

    private sealed record JiraProjectPage([property: JsonPropertyName("values")] JiraProjectPayload[] Values);
    private sealed record JiraProjectPayload(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("archived")] bool Archived);
    private sealed record JiraIssuePage([property: JsonPropertyName("issues")] JiraIssuePayload[] Issues);
    private sealed record JiraIssuePayload(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("fields")] JiraIssueFields? Fields);
    private sealed record JiraIssueFields(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("project")] JiraProjectRef? Project,
        [property: JsonPropertyName("status")] JiraStatusRef? Status);
    private sealed record JiraProjectRef(
        [property: JsonPropertyName("key")] string? Key,
        [property: JsonPropertyName("name")] string? Name);
    private sealed record JiraStatusRef(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("statusCategory")] JiraStatusCategory? StatusCategory)
    {
        public string? CategoryKey => StatusCategory?.Key;
    }
    private sealed record JiraStatusCategory([property: JsonPropertyName("key")] string? Key);
    private sealed record JiraWorklogRequest(
        [property: JsonPropertyName("timeSpentSeconds")] long TimeSpentSeconds,
        [property: JsonPropertyName("started")] string Started,
        [property: JsonPropertyName("comment")] JiraDocument? Comment);
    private sealed record JiraDocument(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("content")] JiraDocumentNode[] Content)
    {
        public static JiraDocument FromText(string text) => new(
            "doc", 1, [new JiraDocumentNode("paragraph", [new JiraTextNode("text", text)])]);
    }
    private sealed record JiraDocumentNode(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("content")] JiraTextNode[] Content);
    private sealed record JiraTextNode(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string Text);
    private sealed record JiraWorklogPayload(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("timeSpentSeconds")] long TimeSpentSeconds,
        [property: JsonPropertyName("started")] string Started);
}
