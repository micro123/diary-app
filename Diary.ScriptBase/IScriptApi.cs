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
    
    // 进度报告
    int CreateBackgroundTask(string title);
    void UpdateBackgroundTask(int which, string message, double progress);
    void FinishBackgroundTask(int which);
    
    // 数据交互
    
}