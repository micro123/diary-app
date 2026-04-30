using Microsoft.Extensions.Logging;

namespace Diary.Utils;

public static class IoUtils
{
    private static ILogger Logger => Logging.Logger;

    public static string ReadAllText(string path)
    {
        if (File.Exists(path))
        {
            var size = new FileInfo(path).Length;
            if (size < (8 << 20))
                return File.ReadAllText(path);
            Logger.LogWarning("文件 {Path}({Size}字节) 超过8MB限制，已跳过", path, size);
        }

        return "";
    }

    public static byte[] ReadAllBytes(string path)
    {
        if (File.Exists(path))
        {
            var size = new FileInfo(path).Length;
            if (size < (8 << 20))
                return File.ReadAllBytes(path);
            Logger.LogWarning("文件 {Path}({Size}字节) 超过8MB限制，已跳过", path, size);
        }

        return [];
    }

    public static bool WriteAllText(string path, string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                File.WriteAllText(path, text);
                return true;
            }
        }
        return false;
    }

    public static bool WriteAllBytes(string path, byte[] bytes)
    {
        if (bytes.Length > 0)
        {
            if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                File.WriteAllBytes(path, bytes);
                return true;
            }
        }
        return false;
    }
}
