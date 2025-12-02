using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class SurveyConfig
{
    [ConfigureSwitch("启用功能")] public bool Enabled { get; set; } = false;
    [ConfigureSwitch("作为服务端")] public bool AsServer { get; set; } = false;
    [ConfigureText("服务端地址")] public string ServerAddress { get; set; } = string.Empty;
    
    /// <summary>
    /// 这里只是是否作为服务端
    /// </summary>
    public bool IsServerEnabled => Enabled && AsServer;
}