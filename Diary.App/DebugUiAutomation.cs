#if DEBUG
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Diagnostics.Cdp;
using Diary.Utils;

namespace Diary.App;

internal static class DebugUiAutomation
{
    internal const string PortEnvironmentVariable = "DIARY_CDP_PORT";
    internal const string RootEnvironmentVariable = "DIARY_UI_TEST_ROOT";
    private static bool _started;

    public static string ConfigureProcess(string appId)
    {
        var configuredRoot = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot))
            return appId;

        if (!Path.IsPathFullyQualified(configuredRoot))
            throw new ArgumentException("UI 测试根目录必须是绝对路径。", RootEnvironmentVariable);

        var root = Path.GetFullPath(configuredRoot);
        FsTools.SetApplicationRootForCurrentProcess(root);
        Trace.WriteLine($"UI 测试数据已隔离到：{root}");
        return CreateIsolatedAppId(appId, root);
    }

    public static void Start()
    {
        if (_started || !TryGetPort(out var port))
            return;

        CdpServer.Start(port);
        _started = true;
        Trace.WriteLine($"Avalonia CDP 调试服务已启动：http://127.0.0.1:{port}");
    }

    public static void Stop()
    {
        if (!_started)
            return;

        CdpServer.Stop();
        _started = false;
    }

    internal static string CreateIsolatedAppId(string appId, string root)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..12];
        return $"{appId}.UiTest.{hash}";
    }

    internal static bool TryGetPort(out int port)
    {
        return TryParsePort(Environment.GetEnvironmentVariable(PortEnvironmentVariable), out port);
    }

    internal static bool TryParsePort(string? value, out int port)
    {
        return int.TryParse(value, out port) && port is >= 1024 and <= 65535;
    }
}
#endif
