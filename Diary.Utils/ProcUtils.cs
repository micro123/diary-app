using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Diary.Utils;

public static class ProcUtils
{
    /// <summary>
    /// 跨平台打开文件
    /// </summary>
    public static void OpenFileCrossPlatform(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"文件不存在: {filePath}");

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", $"\"{filePath}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", $"\"{filePath}\"");
            }
            else
            {
                throw new PlatformNotSupportedException("不支持的操作系统平台");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"打开文件失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 跨平台打开URL
    /// </summary>
    public static void OpenUrlCrossPlatform(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL不能为空");

        // 确保URL包含协议
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            url = "https://" + url;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                throw new PlatformNotSupportedException("不支持的操作系统平台");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"打开URL失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 重启当前程序：以同样的可执行文件路径与命令行参数启动一个新实例，然后退出当前进程。
    /// 注意：本方法直接终止进程，不会经过 Avalonia 的 ShutdownRequested/PreShutdown 流程，
    /// 调用方应在此之前自行完成配置保存等清理工作（例如调用 EasySaveLoad.Save）。
    /// </summary>
    public static void Restart()
    {
        // 仅在新实例成功启动后才退出当前进程，避免启动失败时把程序也杀掉
        if (TryStartNewInstance())
            Environment.Exit(0);
    }

    /// <summary>
    /// 以当前可执行文件路径与命令行参数启动一个新实例。仅启动，不退出当前进程。
    /// </summary>
    /// <returns>是否成功启动新实例。</returns>
    internal static bool TryStartNewInstance()
    {
        var exePath = Environment.ProcessPath
                      ?? Environment.GetCommandLineArgs().FirstOrDefault();

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            Debug.WriteLine("Restart 失败：无法确定当前可执行文件路径");
            return false;
        }

        // 转发命令行参数（跳过第 0 个，即可执行文件路径本身）
        return TryStartNewInstance(exePath, Environment.GetCommandLineArgs().Skip(1));
    }

    /// <summary>
    /// 用指定可执行文件与参数启动一个新进程。UseShellExecute=false，参数逐个转发。
    /// </summary>
    /// <returns>是否成功启动新进程。</returns>
    internal static bool TryStartNewInstance(string exePath, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
            };
            // ArgumentList 会按平台规则自动处理各参数的引号转义
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"启动新实例失败: {ex.Message}");
            return false;
        }
    }
}
