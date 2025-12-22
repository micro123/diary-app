using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class SurveyConfig
{
    [ConfigureSwitch("启用功能", "即使作为客户端也需要打开此开关。")]
    public bool Enabled { get; set; } = false;

    [ConfigureSwitch("作为服务端", helpTip: "允许客户端连接本机，本机可以发出调查事件，客户端响应。一般是管理人员需要此功能。")]
    public bool AsServer { get; set; } = false;

    [ConfigureText("服务端地址", helpTip: "本机作为客户端，接收哪台主机的调查事件并做出回应。")]
    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>
    /// 这里只是是否作为服务端
    /// </summary>
    public bool IsServerEnabled => Enabled && AsServer;
}