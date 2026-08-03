using Diary.Core.Data.Base;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;

namespace Diary.Database;

/// <summary>
/// RedMine 数据访问契约：从 <see cref="DbInterfaceBase"/> 迁出的 RedMine 方法签名
/// （签名一字不改）。阶段 1 中 <see cref="DbInterfaceBase"/> 仍保留这些方法作为
/// 薄委托（经各 provider 的 RedMineDb 实现本接口），调用方与契约测试零改动。
/// 阶段 2 将由调用方直接经注册表取本接口，删除基类上的委托。
/// </summary>
public interface IRedMineDb
{
    RedMineActivity AddRedMineActivity(int id, string title);
    RedMineIssue AddRedMineIssue(int id, string title, string assignedTo, int project, bool closed = false);
    void UpdateRedMineIssueStatus(int id, bool closed);
    RedMineProject AddRedMineProject(int id, string title, string description);
    void UpdateRedMineProjectStatus(int id, bool closed);
    WorkTimeEntry? WorkItemGetTimeEntry(WorkItem item);
    bool WorkItemWasUploaded(WorkItem item);

    ICollection<RedMineActivity> GetRedMineActivities();
    ICollection<RedMineIssueDisplay> GetRedMineIssues(RedMineProject? project);
    ICollection<RedMineProject> GetRedMineProjects();

    WorkTimeEntry? CreateWorkTimeEntry(int work, int activity, int issus);
    bool UpdateWorkTimeEntry(WorkTimeEntry timeEntry);
}
