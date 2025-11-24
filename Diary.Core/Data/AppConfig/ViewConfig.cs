using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class ViewConfig
{
    [ConfigureChoice("默认配色主题", "Light", "Dark", "Auto")]
    public string DefaultColorTheme { get; set; } = "Auto";
}