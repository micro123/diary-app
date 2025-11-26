using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Diary.RedMine.Response;
using RestSharp;

namespace Diary.RedMine;

public static class RedMineApis
{
    public const int PageSize = 50;
    
    // 项目搜索: GET {base}/search.json?q=<keyword1 keyword2>&projects=1
    public static bool SearchProject([NotNullWhen(true)] out IEnumerable<ProjectInfo>? projects,
        out int total, int page = 0,
        string keyword = "")
    {
        projects = null;
        total = 0;
        var url = ProjectInfo.Search();
        var client = RestTools.BasicClient();
        if (client != null)
        {
            var request = RestTools.HttpGet(url);
            if (!string.IsNullOrEmpty(keyword))
                request.AddQueryParameter("q", keyword);
            request.AddQueryParameter("projects", "1");
            request.AddQueryParameter("limit", PageSize);
            request.AddQueryParameter("offset", page * PageSize);
            var response = client.Execute<ProjectInfo.SearchResult>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Debug.WriteLine($"http status code {response.StatusCode}: {response.ErrorMessage}");
            }
            else
            {
                Debug.WriteLine($"response {response.Content}");
                total =  response.Data!.Total;
                projects = response.Data!.Results;
            }
        }

        return projects != null;
    }

    // 项目信息: GET {base}/projects/{id}.json
    public static bool GetProject([NotNullWhen(true)] out ProjectInfo? project,int id)
    {
        project = null;
        var url = ProjectInfo.Fetch(id);
        var client = RestTools.BasicClient();
        if (client != null)
        {
            var request = RestTools.HttpGet(url);
            var response = client.Execute<ProjectInfo.FetchResult>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Debug.WriteLine($"http status code {response.StatusCode}: {response.ErrorMessage}");
            }
            else
            {
                Debug.WriteLine($"response {response.Content}");
                project = response.Data!.Project;
            }
        }

        return project != null;
    }

    // 问题搜索: GET {base}/issues.json?[assigned_to_id=me&][status_id=open|closed|*&](issue_id=...|subject=~...)
    public static bool SearchIssueByKeywords([NotNullWhen(true)] out IEnumerable<IssueInfo>? issues,
        out int total, bool myIssues = true, bool openOnly = true, int page = 0, string keywords = "")
    {
        issues = null;
        total = 0;
        
        var url = IssueInfo.Query();
        var client = RestTools.BasicClient();
        if (client != null)
        {
            var request = RestTools.HttpGet(url);
            if (myIssues)
                request.AddQueryParameter("assigned_to_id", "me");
            request.AddQueryParameter("status_id", openOnly ? "open" : "*");
            if (!string.IsNullOrEmpty(keywords))
                request.AddQueryParameter("subject", $"~{keywords}");
            
            request.AddQueryParameter("limit", PageSize);
            request.AddQueryParameter("offset", page * PageSize);
            
            var response = client.Execute<IssueInfo.SearchResult>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Debug.WriteLine($"http status code {response.StatusCode}: {response.ErrorMessage}");
            }
            else
            {
                Debug.WriteLine($"response {response.Content}");
                total = response.Data!.Total;
                issues = response.Data.Issues;
            }
        }
        
        return issues != null;
    }
    
    public static bool SearchIssueByIds([NotNullWhen(true)] out IEnumerable<IssueInfo>? issues,
        out int total, bool myIssues = true, bool openOnly = true, int page = 0, string ids = "")
    {
        issues = null;
        total = 0;
        
        var url = IssueInfo.Query();
        var client = RestTools.BasicClient();
        if (client != null)
        {
            var request = RestTools.HttpGet(url);
            if (myIssues)
                request.AddQueryParameter("assigned_to_id", "me");
            request.AddQueryParameter("status_id", openOnly ? "open" : "*");
            if (!string.IsNullOrEmpty(ids))
                request.AddQueryParameter("issue_id", ids);
            
            request.AddQueryParameter("limit", PageSize);
            request.AddQueryParameter("offset", page * PageSize);
            
            var response = client.Execute<IssueInfo.SearchResult>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Debug.WriteLine($"http status code {response.StatusCode}: {response.ErrorMessage}");
            }
            else
            {
                Debug.WriteLine($"response {response.Content}");
                total = response.Data!.Total;
                issues = response.Data.Issues;
            }
        }
        
        return issues != null;
    }

    public static bool GetIssue([NotNullWhen(true)] out IssueInfo? issues, int id)
    {
        issues = null;
        var url = IssueInfo.Fetch(id);
        var client = RestTools.BasicClient();
        if (client != null)
        {
            var request = RestTools.HttpGet(url);
            var response = client.Execute<IssueInfo.FetchResult>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Debug.WriteLine($"http status code {response.StatusCode}: {response.ErrorMessage}");
            }
            else
            {
                Debug.WriteLine($"response {response.Content}");
                issues = response.Data!.Issue;
            }
        }
        
        return issues != null;
    }

    // 创建问题: POST {base}/issues.json <json_data contains: project_id,subject,priority_id>
    public static bool CreateIssue([NotNullWhen(true)] out IssueInfo? issue,
        int projectId, string subject, string description = "", bool assignedToSelf = true)
    {
        issue = null;
        
        var url =  IssueInfo.Query();
        var client = RestTools.BasicClient();
        if (client != null)
        {
            var request = RestTools.HttpPost(url);
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
                Debug.WriteLine($"http status code {response.StatusCode}: {response.ErrorMessage}");
            }
            else
            {
                Debug.WriteLine($"response {response.Content}");
                issue = response.Data!.Issue;
            }
        }
        
        return issue != null;
    }

    // 关闭问题: PUT {base}/issues/{id}.json <json_data contains: status_id = closed>
    public static bool CloseIssue(int id)
    {
        // things broken
        return false;
    }

    // 提交工时: POST {base}/time_entries.json <json_data contains: issue_id,spent_on,hours,activity_id,comments>
    public static bool CreateTimeEntry([NotNullWhen(true)] out TimeInfo? timeInfo, int issue, int activity, string date, double hours, string comment)
    {
        timeInfo = null;        
        
        var url = TimeInfo.Query();
        var client = RestTools.BasicClient();
        if (client != null)
        {
            var request = RestTools.HttpPost(url);
            var body = new TimeInfo.PostRes(issue, activity, date, comment, hours);
            request.AddJsonBody(body);
            var response = client.Execute<TimeInfo.PostResult>(request);
            if (response.StatusCode != HttpStatusCode.Created)
            {
                Debug.WriteLine($"http status code {response.StatusCode}: {response.ErrorMessage}");
            }
            else
            {
                Debug.WriteLine($"response {response.Content}");
                timeInfo = response.Data!.TimeEntry;
            }
        }
        
        return timeInfo !=  null;
    }

    // 查询工时: GET {base}/time_entries.json?user_id=me&from=<date_start>&to=<date_end>
    public static bool GetMyTimeEntries([NotNullWhen(true)] out IEnumerable<TimeInfo>? timeInfos,
        out int total,
        string dateStart = "",  string dateEnd = "", int page = 0)
    {
        timeInfos = null;
        total = 0;
        
        var url = TimeInfo.Query();
        var client = RestTools.BasicClient();
        if (client != null)
        {
            var request = RestTools.HttpGet(url);
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
                Debug.WriteLine($"http status code {response.StatusCode}: {response.ErrorMessage}");
            }
            else
            {
                Debug.WriteLine($"response {response.Content}");
                total = response.Data!.Total;
                timeInfos = response.Data!.TimeEntries;
            }
        }
        
        return timeInfos != null;
    }

    // 获取活动列表: GET {base}/enumerations/time_entry_activities.json
    public static bool GetActivities([NotNullWhen(true)] out IEnumerable<ActivityInfo>? activities)
    {
        activities = null;
        var url = ActivityInfo.Query();
        var client = RestTools.BasicClient();
        if (client != null)
        {
            var request = RestTools.HttpGet(url);
            var response = client.Execute<ActivityInfo.Res>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Debug.WriteLine($"http status code {response.StatusCode}: {response.ErrorMessage}");
            }
            else
            {
                activities = response.Data!.TimeEntryActivities;
            }
        }

        return activities != null;
    }

    // 获取账号信息: GET {base}/users/current.json
    public static bool GetUserInfo([NotNullWhen(true)] out UserInfo? userInfo)
    {
        userInfo = null;
        var url = UserInfo.Query();
        var client = RestTools.BasicClient();
        if (client != null)
        {
            var request = RestTools.HttpGet(url);
            var response = client.Execute<UserInfo.Res>(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Debug.WriteLine($"http status code {response.StatusCode}: {response.ErrorMessage}");
            }
            else
            {
                userInfo = response.Data!.User;
            }
        }

        return userInfo != null;
    }
}