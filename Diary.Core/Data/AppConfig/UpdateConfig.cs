using Diary.Core.Configure;
using Diary.Core.Constants;

namespace Diary.Core.Data.AppConfig;

public sealed class UpdateConfig
{
    [ConfigureSwitch("自动检查更新", "应用启动后在后台检查；网络失败不会阻止启动。")]
    public bool AutoCheck { get; set; } = true;

    [ConfigureText("更新服务器地址", helpTip: "局域网 Python 更新服务的 HTTP/HTTPS 根地址，例如 http://192.168.1.40:18080。")]
    public string ServerUrl { get; set; } = "http://127.0.0.1:18080";

    [ConfigureChoice("更新频道", "正式版本使用 stable，alpha/beta/rc/preview 使用 preview。", "stable", "preview")]
    public string Channel { get; set; } = "preview";

    [ConfigureChoice("安装包类型", "Auto 会在 Windows 安装目录包含 python/ 时选择 python313，否则选择 standard。", "Auto", "standard", "python313")]
    public string Flavor { get; set; } = "Auto";

    [ConfigureButton("检查更新", "立即检查", CommandNames.CheckForUpdates, "从配置的更新服务器检查当前 RID 和包类型的最新版本。")]
    private object? CheckForUpdatesButton { get; }
}
