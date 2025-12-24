using Diary.Core.Data.App;
using Diary.Core.Data.Base;
using Diary.Core.Data.Display;
using Diary.Core.Data.RedMine;

namespace Diary.ScriptBase;

public interface IScriptApi
{
    // 程序信息
    string AppName();
    string AppVersion();
    string DataVersion();

    // 日志记录
    void LogDebug(string msg);
    void LogInfo(string msg);
    void LogWarn(string msg);
    void LogError(string msg);
    
    // 程序交互
    void ShowMessage(string title, string body);
    void ShowToast(string info);
    bool Confirm(string title, string body);
    
    // 进度报告
    int CreateBackgroundTask(string title);
    void UpdateBackgroundTask(int which, string message, double progress);
    void FinishBackgroundTask(int which);
    
    // 数据查询
    ICollection<WorkItem> GetWorkItems(string date);
    ICollection<WorkItem> GetWorkItemsByRange(string startDate, string endDate);
    ICollection<WorkTag>  GetTags();
    ICollection<int> GetWorkTags(int work);
    ICollection<Template>  GetTemplates();
    
    // RedMine数据查询
    ICollection<RedMineActivity> GetRedMineActivities();
    ICollection<RedMineIssueDisplay> GetRedMineIssues();
    
    // 事件创建
    WorkItem CreateWorkItem(string date, string title, double hours);
    WorkItem CreateWorkWithTemplate(string date, string title, double hours, string template);
    
    // 工具
    string GetToday();
    string GetTomorrow();
    string GetYesterday();
    
    void CopyText(string text);
}