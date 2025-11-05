using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class RedMineConfig
{
    [ConfigureText("RedMine Server Url")]
    public string RedMineServerUrl { get; set; } = "";
    
    [ConfigureText("RedMine Api Key", true)]
    public string RedMineApiKey { get; set; } = "";
}
