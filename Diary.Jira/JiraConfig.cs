using Diary.Core.Configure;

namespace Diary.Jira;

public class JiraConfig
{
    [ConfigureText("Jira 服务地址", helpTip: "例如 https://company.atlassian.net")]
    public string ServerUrl { get; set; } = string.Empty;

    [ConfigureText("账号或邮箱")]
    public string UserName { get; set; } = string.Empty;

    [ConfigureText("API Token", true, "Jira Cloud 使用账号邮箱和 API Token；自托管 Jira 可填写 Personal Access Token")]
    public string ApiToken { get; set; } = string.Empty;

    [ConfigureSwitch("使用 Bearer Token", "自托管 Jira 使用 Bearer Token 时启用；Jira Cloud 使用账号/API Token 时保持关闭")]
    public bool UseBearerToken { get; set; }

    public bool Valid() =>
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && !string.IsNullOrWhiteSpace(ApiToken)
        && (UseBearerToken || !string.IsNullOrWhiteSpace(UserName));
}
