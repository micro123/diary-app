using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class RedMineConfig
{
    [ConfigureText("服务地址")]
    public string RedMineServerUrl { get; set; } = "";
    
    [ConfigureText("Api Key", true)]
    public string RedMineApiKey { get; set; } = "";
}
