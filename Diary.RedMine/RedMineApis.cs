using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Diary.RedMine.Response;
using RestSharp;

namespace Diary.RedMine;

public static class RedMineApis
{
    // 项目搜索: GET {base}/search.json?q=<keyword1 keyword2>&projects=1

    // 项目信息: GET {base}/projects/{id}.json

    // 问题搜索: GET {base}/issues.json?[assigned_to_id=me&][status_id=open|closed|*&](issue_id=...|subject=~...)

    // 创建问题: POST {base}/issues.json <json_data contains: project_id,subject,priority_id>

    // 关闭问题: PUT {base}/issues/{id}.json <json_data contains: status_id = closed>

    // 提交工时: POST {base}/time_entries.json <json_data contains: issue_id,spent_on,hours,activity_id,comments>

    // 查询工时: GET {base}/time_entries.json?user_id=me&from=<date_start>&to=<date_end>

    // 获取活动列表: GET {base}/enumerations/time_entry_activities.json

    // 获取账号信息: GET {base}/users/current.json
    public static bool GetUserInfo([NotNullWhen(true)] out UserInfo? userInfo)
    {
        userInfo = null;
        var url = UserInfo.Query();
        var client = RestTools.BasicClient();
        if (client != null)
        {
            var request = RestTools.HttpGet(url);
            var response = client.Execute(request);
            Debug.WriteLine(response.Content);
        }

        return false;
    }
}
