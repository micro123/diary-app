using Diary.Core.Configure;
using Diary.Core.Constants;

namespace Diary.Core.Data.AppConfig;

public class ViewConfig
{
    [ConfigureUser("界面字体", "APP_FONT", "默认使用应用自带的 Noto Sans Mono CJK SC，也可跟随系统、选择已安装字体或加载 .ttf/.otf 字体文件；保存后立即生效，字体不可用时安全回退。")]
    public string FontSource { get; set; } = AppFontSource.BundledDefault;

    public string SystemFontFamily { get; set; } = string.Empty;

    public string FontFilePath { get; set; } = string.Empty;

    [ConfigureChoice("默认配色主题", "当前只有亮色和暗色两种色调。", "Light", "Dark", "Auto")]
    public string DefaultColorTheme { get; set; } = "Auto";

    [ConfigureSwitch("始终显示托盘", helpTip: "在托盘区显示托盘，不管有没有关闭主程序。")]
    public bool AlwaysShowTrayIcon { get; set; } = true;

    [ConfigureSwitch("隐藏到托盘", "开启时关闭主界面将隐藏到托盘而不是退出程序。")]
    public bool HideToTray { get; set; } = false;

    [ConfigureSwitch("显示开发者功能", "显示脚本管理页和脚本诊断入口；普通记录用户通常不需要开启。")]
    public bool ShowDeveloperFeatures { get; set; } = false;

    [ConfigureButton("首次使用引导", "重新打开", CommandNames.ShowOnboarding, "手动重新查看本地保存、远程同步和日常效率说明。")]
    private object? ShowOnboardingGuide { get; }

    public bool HasCompletedOnboarding { get; set; } = false;

    /// <summary>最近使用的主标签 ID，供新建工作项排序使用。</summary>
    public List<int> RecentPrimaryTagIds { get; set; } = new();
}
