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

    public static void Restart()
    {
        throw new NotImplementedException();
    }
}
