using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class ViewConfig
{
    [ConfigureSwitch("Show closed issues")]
    public bool ShowClosedIssues { get; set; } = false;
    
    [ConfigureText("Default Task Title")]
    public string DefaultTaskTitle { get; set; } = "";
}