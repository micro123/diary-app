using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public sealed class ScriptConfig
{
    [ConfigureSwitch(
        "允许在主进程内执行脚本",
        "默认关闭。开启后，支持的 C# 和旧版脚本会在主进程内执行；Lua、Python 等未提供进程内运行时的脚本仍使用 Worker。脚本异常或资源耗尽可能影响主程序。")]
    public bool UseInProcessExecution { get; set; }
}
