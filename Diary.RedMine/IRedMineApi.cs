using Diary.RedMine.Response;

namespace Diary.RedMine;

/// <summary>
/// RedMine REST 访问契约。从原静态 <c>RedMineApis</c> 抽出为接口，便于 DI 注入与替换/测试。
/// 所有方法返回 bool 表示成功，结果经 out 参数给出（与原静态 API 一致）。
/// </summary>
public interface IRedMineApi
{
    /// <summary>分页大小（原 RedMineApis.PageSize）。</summary>
    int PageSize { get; }

    bool SearchProject(out IEnumerable<ProjectInfo>? projects, out int total, int page = 0, string keyword = "");
    bool GetProject(out ProjectInfo? project, int id);
    bool SearchIssueByKeywords(out IEnumerable<IssueInfo>? issues, out int total, bool myIssues = true, bool openOnly = true, int page = 0, string keywords = "");
    bool SearchIssueByIds(out IEnumerable<IssueInfo>? issues, out int total, bool myIssues = true, bool openOnly = true, int page = 0, string ids = "");
    bool GetIssue(out IssueInfo? issue, int id);
    bool CreateIssue(out IssueInfo? issue, int projectId, string subject, string description = "", bool assignedToSelf = true);
    bool CloseIssue(int id);
    bool CreateTimeEntry(out TimeInfo? timeInfo, int issue, int activity, string date, double hours, string comment);
    bool GetMyTimeEntries(out IEnumerable<TimeInfo>? timeInfos, out int total, string dateStart = "", string dateEnd = "", int page = 0);
    bool GetActivities(out IEnumerable<ActivityInfo>? activities);
    bool GetUserInfo(out UserInfo? userInfo);
}
