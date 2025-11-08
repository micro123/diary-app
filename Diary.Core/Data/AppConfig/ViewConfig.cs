using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class ViewConfig
{
    [ConfigureSwitch("显示已关闭")] public bool ShowClosedIssues { get; set; } = false;

    [ConfigureSwitch("使用月视图")] public bool UseMonthView { get; set; } = true;

    [ConfigureChoice("默认配色主题", "Light", "Dark", "Auto")]
    public string DefaultColorTheme { get; set; } = "Auto";
}