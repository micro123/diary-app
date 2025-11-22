using Diary.Core.Configure;
using Diary.Core.Constants;

namespace Diary.Core.Data.AppConfig;

public class WorkConfig
{
    [ConfigureText("默认事项名称")]
    public string DefaultTaskTitle { get; set; } = "";
    
    [ConfigureReal("每天工作时长", 0, 24)]
    public double DefaultDailyTotalHours { get; set; } = 8.0;
    
    [ConfigureButton("标签管理", "编辑标签", CommandNames.EditWorkTags)]
    private int EditWorkTags { get; set; } = 0;
    
}