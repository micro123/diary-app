using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class ViewConfig
{
    [ConfigureChoice("默认配色主题", "当前只有亮色和暗色两种色调。", "Light", "Dark", "Auto")]
    public string DefaultColorTheme { get; set; } = "Auto";

    [ConfigureSwitch("始终显示托盘", helpTip: "在托盘区显示托盘，不管有没有关闭主程序。")]
    public bool AlwaysShowTrayIcon { get; set; } = true;

    [ConfigureSwitch("隐藏到托盘", "开启时关闭主界面将隐藏到托盘而不是退出程序。")]
    public bool HideToTray { get; set; } = false;
}