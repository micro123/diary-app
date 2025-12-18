using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class RedMineConfig
{
    [ConfigureText("服务地址")] public string RedMineServerUrl { get; set; } = "";

    [ConfigureText("Api Key", true, "可以在 ”主页 > 我的账号“ 右侧找到 ”API访问键“")]
    public string RedMineApiKey { get; set; } = "";

    [ConfigureSwitch("使用代理服务器", "如果不能直接访问，可以使用代理试试")]
    public bool EnableProxy { get; set; } = false;

    [ConfigureText("代理服务器地址", helpTip: "代理服务器，通常为 http://ip:port/ 格式")]
    public string ProxyServer { get; set; } = "";

    // helper for check config valid
    public bool Valid()
    {
        return !string.IsNullOrEmpty(RedMineServerUrl) && !string.IsNullOrEmpty(RedMineApiKey) &&
               (!EnableProxy || !string.IsNullOrEmpty(ProxyServer));
    }
}