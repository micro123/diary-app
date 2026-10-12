using Diary.RedMine.Response;

using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Diary.RedMine;

/// <summary>
/// RedMine REST 访问契约。从原静态 <c>RedMineApis</c> 抽出为接口，便于 DI 注入与替换/测试。
/// 所有方法返回 bool 表示成功，结果经 out 参数给出（与原静态 API 一致）。
/// </summary>
public interface IRedMineApi
{
    /// <summary>分页大小（原 RedMineApis.PageSize）。</summary>
    int PageSize { get; }

    /// <summary>最近一次失败的脱敏诊断；测试替身和旧实现可不提供。</summary>
    RedMineApiFailure? LastFailure => null;

    bool SearchProject([NotNullWhen(true)] out IEnumerable<ProjectInfo>? projects, out int total, int page = 0, string keyword = "");
    bool GetProject([NotNullWhen(true)] out ProjectInfo? project, int id);
    bool SearchIssueByKeywords([NotNullWhen(true)] out IEnumerable<IssueInfo>? issues, out int total, bool myIssues = true, bool openOnly = true, int page = 0, string keywords = "");
    bool SearchIssueByIds([NotNullWhen(true)] out IEnumerable<IssueInfo>? issues, out int total, bool myIssues = true, bool openOnly = true, int page = 0, string ids = "");
    bool GetIssue([NotNullWhen(true)] out IssueInfo? issue, int id);
    bool CreateIssue([NotNullWhen(true)] out IssueInfo? issue, int projectId, string subject, string description = "", bool assignedToSelf = true);
    bool CloseIssue(int id);
    bool CreateTimeEntry([NotNullWhen(true)] out TimeInfo? timeInfo, int issue, int activity, string date, double hours, string comment);
    bool GetMyTimeEntries([NotNullWhen(true)] out IEnumerable<TimeInfo>? timeInfos, out int total, string dateStart = "", string dateEnd = "", int page = 0);
    bool GetActivities([NotNullWhen(true)] out IEnumerable<ActivityInfo>? activities);
    bool GetUserInfo([NotNullWhen(true)] out UserInfo? userInfo);
}

public sealed record RedMineApiFailure(
    string Operation,
    string Message,
    HttpStatusCode? StatusCode,
    string? ResponseStatus,
    string? ErrorType,
    DateTimeOffset OccurredAtUtc);

public static class RedMineApiFailureExtensions
{
    public static string DescribeLastFailure(this IRedMineApi api, string fallback)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        var detail = api.LastFailure?.Message;
        return string.IsNullOrWhiteSpace(detail) ? fallback : $"{fallback}：{detail}";
    }
}
