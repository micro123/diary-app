using System.Diagnostics.CodeAnalysis;
using System.Net;
using Diary.RedMine.Response;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using RestSharp;

namespace Diary.RedMine;

/// <summary>
/// <see cref="IRedMineApi"/> 实现。从原静态 <c>RedMineApis</c> 迁出，方法体逐行一致，
/// 仍走 <see cref="RestTools"/>（同程序集 internal 静态），日志仅记录结构化摘要，不记录响应正文。
/// 客户端释放与在途请求通过租约协调，配置重载不会释放仍在使用的 HTTP 客户端。
/// </summary>
public class RedMineApi : IRedMineApi, IDisposable
{
    private const int ClosedStatusId = 5;
    private readonly RedMineConfig _configuration;
    private readonly ILogger _logger;
    private readonly SharedDisposableResource<RestClient>? _clientLifetime;

    public RedMineApi(RedMineConfig? configuration = null, ILogger<RedMineApi>? logger = null)
    {
        _configuration = configuration
            ?? RedMineConfigurationStore.Current.Instances.FirstOrDefault()
            ?? new RedMineInstanceSettings();
        _logger = logger ?? Logging.Factory.CreateLogger<RedMineApi>();
        var client = RestTools.BasicClient(_configuration);
        _clientLifetime = client is null ? null : new SharedDisposableResource<RestClient>(client);
    }
    public int PageSize => 50;

    // 项目搜索: GET {base}/search.json?q=<keyword1 keyword2>&projects=1
    public bool SearchProject([NotNullWhen(true)] out IEnumerable<ProjectInfo>? projects,
        out int total, int page = 0,
        string keyword = "")
    {
        projects = null;
        total = 0;
        string url;
        url = !string.IsNullOrEmpty(keyword) ? ProjectInfo.Search() : ProjectInfo.All();
        using var clientLease = _clientLifetime?.TryAcquire();
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpGet(_configuration, url);
            if (!string.IsNullOrEmpty(keyword))
                request.AddQueryParameter("q", keyword);
            request.AddQueryParameter("projects", "1");
            request.AddQueryParameter("limit", PageSize);
            request.AddQueryParameter("offset", page * PageSize);
            var response = client.Execute<ProjectInfo.SearchResult>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError("http status code {StatusCode}: {ErrorMessage}", response.StatusCode, response.ErrorMessage);
            }
            else
            {
                total = response.Data!.Total;
                projects = response.Data.Projects;
                _logger.LogDebug("Loaded {Count} RedMine projects (total {Total})", projects.Count(), total);
            }
        }

        return projects != null;
    }

    // 项目信息: GET {base}/projects/{id}.json
    public bool GetProject([NotNullWhen(true)] out ProjectInfo? project, int id)
    {
        project = null;
        var url = ProjectInfo.Fetch(id);
        using var clientLease = _clientLifetime?.TryAcquire();
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpGet(_configuration, url);
            var response = client.Execute<ProjectInfo.FetchResult>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError("http status code {StatusCode}: {ErrorMessage}", response.StatusCode, response.ErrorMessage);
            }
            else
            {
                project = response.Data!.Project;
                _logger.LogDebug("Loaded RedMine project {ProjectId}", project.Id);
            }
        }

        return project != null;
    }

    // 问题搜索: GET {base}/issues.json?[assigned_to_id=me&][status_id=open|closed|*&](issue_id=...|subject=~...)
    public bool SearchIssueByKeywords([NotNullWhen(true)] out IEnumerable<IssueInfo>? issues,
        out int total, bool myIssues = true, bool openOnly = true, int page = 0, string keywords = "")
    {
        string? paramValue = !string.IsNullOrEmpty(keywords) ? $"~{keywords}" : null;
        return SearchIssuesInternal(out issues, out total, myIssues, openOnly, page, "subject", paramValue);
    }

    public bool SearchIssueByIds([NotNullWhen(true)] out IEnumerable<IssueInfo>? issues,
        out int total, bool myIssues = true, bool openOnly = true, int page = 0, string ids = "")
    {
        string? paramValue = !string.IsNullOrEmpty(ids) ? ids : null;
        return SearchIssuesInternal(out issues, out total, myIssues, openOnly, page, "issue_id", paramValue);
    }

    private bool SearchIssuesInternal([NotNullWhen(true)] out IEnumerable<IssueInfo>? issues,
        out int total, bool myIssues, bool openOnly, int page,
        string queryParamName, string? queryParamValue)
    {
        issues = null;
        total = 0;

        var url = IssueInfo.Query();
        using var clientLease = _clientLifetime?.TryAcquire();
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpGet(_configuration, url);
            if (myIssues)
                request.AddQueryParameter("assigned_to_id", "me");
            request.AddQueryParameter("status_id", openOnly ? "open" : "*");
            if (queryParamValue != null)
                request.AddQueryParameter(queryParamName, queryParamValue);

            request.AddQueryParameter("limit", PageSize);
            request.AddQueryParameter("offset", page * PageSize);

            var response = client.Execute<IssueInfo.SearchResult>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError("http status code {StatusCode}: {ErrorMessage}", response.StatusCode, response.ErrorMessage);
            }
            else
            {
                total = response.Data!.Total;
                issues = response.Data.Issues;
                _logger.LogDebug("Loaded {Count} RedMine issues (total {Total})", issues.Count(), total);
            }
        }

        return issues != null;
    }

    public bool GetIssue([NotNullWhen(true)] out IssueInfo? issues, int id)
    {
        issues = null;
        var url = IssueInfo.Fetch(id);
        using var clientLease = _clientLifetime?.TryAcquire();
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpGet(_configuration, url);
            var response = client.Execute<IssueInfo.FetchResult>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError("http status code {StatusCode}: {ErrorMessage}", response.StatusCode, response.ErrorMessage);
            }
            else
            {
                issues = response.Data!.Issue;
                _logger.LogDebug("Loaded RedMine issue {IssueId}", issues.Id);
            }
        }

        return issues != null;
    }

    // 创建问题: POST {base}/issues.json <json_data contains: project_id,subject,priority_id>
    public bool CreateIssue([NotNullWhen(true)] out IssueInfo? issue,
        int projectId, string subject, string description = "", bool assignedToSelf = true)
    {
        issue = null;

        var url = IssueInfo.Query();
        using var clientLease = _clientLifetime?.TryAcquire();
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpPost(_configuration, url);
            var postData = new IssueInfo.PostRes(projectId, subject);
            if (!string.IsNullOrEmpty(description))
            {
                postData.Data.Description = description;
            }
            if (assignedToSelf)
            {
                postData.Data.AssignedToId = "me";
            }
            request.AddJsonBody(postData);

            var response = client.Execute<IssueInfo.FetchResult>(request);
            if (response.StatusCode != HttpStatusCode.Created)
            {
                _logger.LogError("http status code {StatusCode}: {ErrorMessage}", response.StatusCode, response.ErrorMessage);
            }
            else
            {
                issue = response.Data!.Issue;
                _logger.LogDebug("Created RedMine issue {IssueId}", issue.Id);
            }
        }

        return issue != null;
    }

    // 关闭问题: PUT {base}/issues/{id}.json <json_data contains: status_id = closed>
    public bool CloseIssue(int id)
    {
        if (id <= 0)
            return false;

        using var clientLease = _clientLifetime?.TryAcquire();
        if (clientLease is null)
            return false;

        var request = RestTools.HttpPut(_configuration, IssueInfo.Fetch(id));
        request.AddJsonBody(new IssueInfo.PutRes(ClosedStatusId));
        var response = clientLease.Resource.Execute(request);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
        {
            _logger.LogError("http status code {StatusCode}: {ErrorMessage}", response.StatusCode, response.ErrorMessage);
            return false;
        }

        _logger.LogDebug("closed issue {IssueId}", id);
        return true;
    }

    // 提交工时: POST {base}/time_entries.json <json_data contains: issue_id,spent_on,hours,activity_id,comments>
    public bool CreateTimeEntry([NotNullWhen(true)] out TimeInfo? timeInfo, int issue, int activity, string date, double hours, string comment)
    {
        timeInfo = null;

        var url = TimeInfo.Query();
        using var clientLease = _clientLifetime?.TryAcquire();
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpPost(_configuration, url);
            var body = new TimeInfo.PostRes(issue, activity, date, comment, hours);
            request.AddJsonBody(body);
            var response = client.Execute<TimeInfo.PostResult>(request);
            if (response.StatusCode != HttpStatusCode.Created)
            {
                _logger.LogError("http status code {StatusCode}: {ErrorMessage}", response.StatusCode, response.ErrorMessage);
            }
            else
            {
                timeInfo = response.Data!.TimeEntry;
                _logger.LogDebug("Created RedMine time entry {TimeEntryId}", timeInfo.Id);
            }
        }

        return timeInfo != null;
    }

    // 查询工时: GET {base}/time_entries.json?user_id=me&from=<date_start>&to=<date_end>
    public bool GetMyTimeEntries([NotNullWhen(true)] out IEnumerable<TimeInfo>? timeInfos,
        out int total,
        string dateStart = "", string dateEnd = "", int page = 0)
    {
        timeInfos = null;
        total = 0;

        var url = TimeInfo.Query();
        using var clientLease = _clientLifetime?.TryAcquire();
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpGet(_configuration, url);
            request.AddQueryParameter("user_id", "me");
            request.AddQueryParameter("limit", PageSize);
            request.AddQueryParameter("offset", page * PageSize);
            if (!string.IsNullOrEmpty(dateStart))
                request.AddQueryParameter("from", dateStart);
            if (!string.IsNullOrEmpty(dateEnd))
                request.AddQueryParameter("to", dateEnd);

            var response = client.Execute<TimeInfo.QueryResult>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError("http status code {StatusCode}: {ErrorMessage}", response.StatusCode, response.ErrorMessage);
            }
            else
            {
                total = response.Data!.Total;
                timeInfos = response.Data.TimeEntries;
                _logger.LogDebug("Loaded {Count} RedMine time entries (total {Total})", timeInfos.Count(), total);
            }
        }

        return timeInfos != null;
    }

    // 获取活动列表: GET {base}/enumerations/time_entry_activities.json
    public bool GetActivities([NotNullWhen(true)] out IEnumerable<ActivityInfo>? activities)
    {
        activities = null;
        var url = ActivityInfo.Query();
        using var clientLease = _clientLifetime?.TryAcquire();
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpGet(_configuration, url);
            var response = client.Execute<ActivityInfo.Res>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError("http status code {StatusCode}: {ErrorMessage}", response.StatusCode, response.ErrorMessage);
            }
            else
            {
                activities = response.Data!.TimeEntryActivities;
                _logger.LogDebug("Loaded {Count} RedMine time entry activities", activities.Count());
            }
        }

        return activities != null;
    }

    // 获取账号信息: GET {base}/users/current.json
    public bool GetUserInfo([NotNullWhen(true)] out UserInfo? userInfo)
    {
        userInfo = null;
        var url = UserInfo.Query();
        using var clientLease = _clientLifetime?.TryAcquire();
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpGet(_configuration, url);
            var response = client.Execute<UserInfo.Res>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogError("http status code {StatusCode}: {ErrorMessage}", response.StatusCode, response.ErrorMessage);
            }
            else
            {
                userInfo = response.Data!.User;
                _logger.LogDebug("Loaded RedMine current user {UserId}/{Login}", userInfo.Id, userInfo.Login);
            }
        }

        return userInfo != null;
    }

    public void Dispose() => _clientLifetime?.Dispose();
}
