using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class RedMineConfig
{
    [ConfigureText("服务地址")]
    public string RedMineServerUrl { get; set; } = "";
    
    [ConfigureText("Api Key", true)]
    public string RedMineApiKey { get; set; } = "";
    
    [ConfigureSwitch("使用代理服务器")]
    public bool EnableProxy { get; set; } = false;
    
    [ConfigureText("代理服务器地址")]
    public string ProxyServer { get; set; } = "";
}
