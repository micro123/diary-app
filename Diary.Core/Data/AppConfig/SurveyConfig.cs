using Diary.Core.Configure;

namespace Diary.Core.Data.AppConfig;

public class SurveyConfig
{
    [ConfigureSwitch("启用调查功能", "作为调查者或受访者时都需要打开此开关。")]
    public bool Enabled { get; set; } = false;

    [ConfigureSwitch("作为调查者", helpTip: "调查者展示调查页面并向受访者发起调查，不需要填写调查者 IP 地址。")]
    public bool AsServer { get; set; } = false;

    [ConfigureText("调查者 IP 地址", helpTip: "仅作为受访者时填写调查者的 IP 地址，调查者使用固定端口 9721；作为调查者时留空即可。")]
    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>
    /// 当前应用是否以调查者角色运行。
    /// </summary>
    public bool IsServerEnabled => Enabled && AsServer;

    /// <summary>
    /// 当前应用是否以受访者角色运行。
    /// </summary>
    public bool IsRespondentEnabled => Enabled && !AsServer;

    public bool TryGetRespondentAddress(out string address)
    {
        address = ServerAddress.Trim();
        return IsRespondentEnabled && address.Length > 0;
    }
}
