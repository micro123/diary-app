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
    private readonly string _instanceId;
    private readonly string _serverSummary;
    private RedMineApiFailure? _lastFailure;

    public RedMineApi(RedMineConfig? configuration = null, ILogger<RedMineApi>? logger = null)
    {
        _configuration = configuration
            ?? RedMineConfigurationStore.Current.Instances.FirstOrDefault()
            ?? new RedMineInstanceSettings();
        _logger = logger ?? Logging.Factory.CreateLogger<RedMineApi>();
        _instanceId = (_configuration as RedMineInstanceSettings)?.InstanceId
            ?? RedMinePluginConstants.DefaultInstanceId;
        _serverSummary = DescribeServer(_configuration.RedMineServerUrl);
        var client = RestTools.BasicClient(_configuration);
        _clientLifetime = client is null ? null : new SharedDisposableResource<RestClient>(client);
    }
    public int PageSize => 50;
    public RedMineApiFailure? LastFailure => Volatile.Read(ref _lastFailure);

    // 项目搜索: GET {base}/search.json?q=<keyword1 keyword2>&projects=1
    public bool SearchProject([NotNullWhen(true)] out IEnumerable<ProjectInfo>? projects,
        out int total, int page = 0,
        string keyword = "")
    {
        projects = null;
        total = 0;
        string url;
        url = !string.IsNullOrEmpty(keyword) ? ProjectInfo.Search() : ProjectInfo.All();
        using var clientLease = AcquireClient("搜索项目", Method.Get, url);
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
            if (EnsureSuccess(response, "搜索项目", request.Method, url, HttpStatusCode.OK)
                && EnsureData(response, "搜索项目", request.Method, url))
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
        using var clientLease = AcquireClient("读取项目", Method.Get, url);
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpGet(_configuration, url);
            var response = client.Execute<ProjectInfo.FetchResult>(request);
            if (EnsureSuccess(response, "读取项目", request.Method, url, HttpStatusCode.OK)
                && EnsureData(response, "读取项目", request.Method, url))
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
        using var clientLease = AcquireClient("搜索问题", Method.Get, url);
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
            if (EnsureSuccess(response, "搜索问题", request.Method, url, HttpStatusCode.OK)
                && EnsureData(response, "搜索问题", request.Method, url))
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
        using var clientLease = AcquireClient("读取问题", Method.Get, url);
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpGet(_configuration, url);
            var response = client.Execute<IssueInfo.FetchResult>(request);
            if (EnsureSuccess(response, "读取问题", request.Method, url, HttpStatusCode.OK)
                && EnsureData(response, "读取问题", request.Method, url))
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
        using var clientLease = AcquireClient("创建问题", Method.Post, url);
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
            if (EnsureSuccess(response, "创建问题", request.Method, url, HttpStatusCode.Created)
                && EnsureData(response, "创建问题", request.Method, url))
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
        {
            RecordLocalFailure("关闭问题", Method.Put, "问题 ID 必须大于 0。");
            return false;
        }

        var url = IssueInfo.Fetch(id);
        using var clientLease = AcquireClient("关闭问题", Method.Put, url);
        if (clientLease is null)
            return false;

        var request = RestTools.HttpPut(_configuration, url);
        request.AddJsonBody(new IssueInfo.PutRes(ClosedStatusId));
        var response = clientLease.Resource.Execute(request);
        if (!EnsureSuccess(response, "关闭问题", request.Method, url, HttpStatusCode.OK, HttpStatusCode.NoContent))
            return false;

        _logger.LogDebug("closed issue {IssueId}", id);
        return true;
    }

    // 提交工时: POST {base}/time_entries.json <json_data contains: issue_id,spent_on,hours,activity_id,comments>
    public bool CreateTimeEntry([NotNullWhen(true)] out TimeInfo? timeInfo, int issue, int activity, string date, double hours, string comment)
    {
        timeInfo = null;

        var url = TimeInfo.Query();
        using var clientLease = AcquireClient("提交工时", Method.Post, url);
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpPost(_configuration, url);
            var body = new TimeInfo.PostRes(issue, activity, date, comment, hours);
            request.AddJsonBody(body);
            var response = client.Execute<TimeInfo.PostResult>(request);
            if (EnsureSuccess(response, "提交工时", request.Method, url, HttpStatusCode.Created)
                && EnsureData(response, "提交工时", request.Method, url))
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
        using var clientLease = AcquireClient("查询工时", Method.Get, url);
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
            if (EnsureSuccess(response, "查询工时", request.Method, url, HttpStatusCode.OK)
                && EnsureData(response, "查询工时", request.Method, url))
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
        using var clientLease = AcquireClient("读取活动列表", Method.Get, url);
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpGet(_configuration, url);
            var response = client.Execute<ActivityInfo.Res>(request);
            if (EnsureSuccess(response, "读取活动列表", request.Method, url, HttpStatusCode.OK)
                && EnsureData(response, "读取活动列表", request.Method, url))
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
        using var clientLease = AcquireClient("读取当前用户", Method.Get, url);
        if (clientLease is not null)
        {
            var client = clientLease.Resource;
            var request = RestTools.HttpGet(_configuration, url);
            var response = client.Execute<UserInfo.Res>(request);
            if (EnsureSuccess(response, "读取当前用户", request.Method, url, HttpStatusCode.OK)
                && EnsureData(response, "读取当前用户", request.Method, url))
            {
                userInfo = response.Data!.User;
                _logger.LogDebug("Loaded RedMine current user {UserId}/{Login}", userInfo.Id, userInfo.Login);
            }
        }

        return userInfo != null;
    }

    private SharedDisposableResource<RestClient>.Lease? AcquireClient(
        string operation,
        Method method,
        string resource)
    {
        Volatile.Write(ref _lastFailure, null);
        var lease = _clientLifetime?.TryAcquire();
        if (lease is not null)
            return lease;

        RecordFailure(
            new RedMineApiFailure(
                operation,
                "Redmine 配置无效、连接已关闭或配置正在重新加载，请检查服务地址、API Key 和代理设置。",
                null,
                null,
                null,
                DateTimeOffset.UtcNow),
            method,
            resource,
            null,
            null);
        return null;
    }

    private bool EnsureSuccess(
        RestResponse response,
        string operation,
        Method method,
        string resource,
        params HttpStatusCode[] expectedStatusCodes)
    {
        if (expectedStatusCodes.Contains(response.StatusCode))
            return true;

        HttpStatusCode? statusCode = response.StatusCode == 0 ? null : response.StatusCode;
        var errorMessage = NormalizeErrorMessage(response.ErrorMessage ?? response.ErrorException?.Message);
        var message = CreateUserMessage(statusCode, response.ResponseStatus.ToString(), errorMessage);
        RecordFailure(
            new RedMineApiFailure(
                operation,
                message,
                statusCode,
                response.ResponseStatus.ToString(),
                response.ErrorException?.GetType().Name,
                DateTimeOffset.UtcNow),
            method,
            resource,
            response.ErrorException,
            errorMessage);
        return false;
    }

    private bool EnsureData<T>(RestResponse<T> response, string operation, Method method, string resource)
    {
        if (response.Data is not null)
            return true;

        const string message = "Redmine 返回了成功状态，但响应数据为空或格式无法解析。";
        RecordFailure(
            new RedMineApiFailure(
                operation,
                message,
                response.StatusCode,
                response.ResponseStatus.ToString(),
                response.ErrorException?.GetType().Name,
                DateTimeOffset.UtcNow),
            method,
            resource,
            response.ErrorException,
            NormalizeErrorMessage(response.ErrorMessage ?? response.ErrorException?.Message));
        return false;
    }

    private void RecordLocalFailure(string operation, Method method, string message)
    {
        Volatile.Write(ref _lastFailure, null);
        RecordFailure(
            new RedMineApiFailure(operation, message, null, null, null, DateTimeOffset.UtcNow),
            method,
            string.Empty,
            null,
            null);
    }

    private void RecordFailure(
        RedMineApiFailure failure,
        Method method,
        string resource,
        Exception? exception,
        string? errorMessage)
    {
        Volatile.Write(ref _lastFailure, failure);
        _logger.LogError(
            exception,
            "Redmine API 请求失败。InstanceId={InstanceId}, Server={Server}, Operation={Operation}, Method={Method}, Resource={Resource}, StatusCode={StatusCode}, ResponseStatus={ResponseStatus}, ErrorType={ErrorType}, ErrorMessage={ErrorMessage}",
            _instanceId,
            _serverSummary,
            failure.Operation,
            method,
            resource,
            failure.StatusCode,
            failure.ResponseStatus,
            failure.ErrorType,
            errorMessage);
    }

    private static string CreateUserMessage(
        HttpStatusCode? statusCode,
        string? responseStatus,
        string? errorMessage)
    {
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"身份验证或权限检查失败（HTTP {(int)statusCode.Value}），请检查 API Key 和 Redmine 权限。",
            HttpStatusCode.NotFound => "请求的 Redmine 资源不存在（HTTP 404）。",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                $"Redmine 拒绝了请求参数（HTTP {(int)statusCode.Value}），请检查填写内容和必填字段。",
            HttpStatusCode.Conflict => "Redmine 检测到数据冲突（HTTP 409），请刷新后重试。",
            HttpStatusCode.TooManyRequests => "Redmine 请求过于频繁（HTTP 429），请稍后重试。",
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                $"Redmine 请求超时（HTTP {(int)statusCode.Value}）。",
            >= HttpStatusCode.InternalServerError =>
                $"Redmine 服务器返回错误（HTTP {(int)statusCode.Value}）。",
            not null => $"Redmine 请求失败（HTTP {(int)statusCode.Value}）。",
            _ when string.Equals(responseStatus, "TimedOut", StringComparison.OrdinalIgnoreCase) =>
                "连接 Redmine 超时，请检查网络、代理和服务地址。",
            _ => "无法连接 Redmine，请检查网络、TLS 证书、代理和服务地址。",
        };
        return string.IsNullOrWhiteSpace(errorMessage) ? message : $"{message} {errorMessage}";
    }

    private string? NormalizeErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (!string.IsNullOrEmpty(_configuration.RedMineApiKey))
            normalized = normalized.Replace(_configuration.RedMineApiKey, "***", StringComparison.Ordinal);
        return normalized.Length <= 300 ? normalized : normalized[..300] + "…";
    }

    private static string DescribeServer(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri))
            return "<invalid>";
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public void Dispose() => _clientLifetime?.Dispose();
}
