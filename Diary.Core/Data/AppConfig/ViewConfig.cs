using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class ViewConfig
{
    [ConfigureSwitch("显示已关闭")]
    public bool ShowClosedIssues { get; set; } = false;
    
    [ConfigureText("默认事项名称")]
    public string DefaultTaskTitle { get; set; } = "";
    
    [ConfigureReal("每天工作时长", 0, 24)]
    public double DefaultDailyTotalHours { get; set; } = 8.0;
    
    [ConfigureSwitch("使用月视图")]
    public bool UseMonthView { get; set; } = true;
}
