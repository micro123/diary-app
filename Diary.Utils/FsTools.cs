using System.Reflection;

namespace Diary.Utils;

public static partial class FsTools
{
    private static readonly Dictionary<string, string> KnownDirectories = new();

    public static string GetBinaryDirectory()
    {
        lock (KnownDirectories)
        {
            if (!KnownDirectories.TryGetValue("AppBinDir", out string? value))
            {
                var assembly = Assembly.GetEntryAssembly();
                var path = assembly!.Location;
                value = Path.GetDirectoryName(path)!;
                KnownDirectories.Add("AppBinDir", value);
            }
            return value;
        }
    }

    private static string GetApplicationName()
    {
        return "Diary.App";
    }

    public static void SetApplicationRootForCurrentProcess(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
            throw new ArgumentException("应用根目录必须是绝对路径。", nameof(rootDirectory));

        var root = Path.GetFullPath(rootDirectory);
        if (string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("应用根目录必须是非磁盘根目录的绝对路径。", nameof(rootDirectory));
        }

        lock (KnownDirectories)
        {
            if (KnownDirectories.ContainsKey("AppCfgDir")
                || KnownDirectories.ContainsKey("AppDataDir")
                || KnownDirectories.ContainsKey("AppTempDir"))
            {
                throw new InvalidOperationException("应用目录已经初始化，不能再切换根目录。");
            }

            var config = Path.Combine(root, "config");
            var data = Path.Combine(root, "data");
            var temporary = Path.Combine(root, "temp");
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(temporary);
            KnownDirectories.Add("AppCfgDir", config);
            KnownDirectories.Add("AppDataDir", data);
            KnownDirectories.Add("AppTempDir", temporary);
        }
    }

    public static string GetApplicationConfigDirectory()
    {
        lock (KnownDirectories)
        {
            if (!KnownDirectories.TryGetValue("AppCfgDir", out string? value))
            {
                var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                appdata = Path.Combine(appdata, GetApplicationName());
                value = appdata;
                Directory.CreateDirectory(value);
                KnownDirectories.Add("AppCfgDir", value);
            }
            return value;
        }
    }

    public static string GetApplicationDataDirectory()
    {
        lock (KnownDirectories)
        {
            if (!KnownDirectories.TryGetValue("AppDataDir", out string? value))
            {
                var appdata = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                appdata = Path.Combine(appdata, GetApplicationName());
                value = appdata;
                Directory.CreateDirectory(value);
                KnownDirectories.Add("AppDataDir", value);
            }
            return value;
        }
    }


    public static string GetTemporaryDirectory()
    {
        lock (KnownDirectories)
        {
            if (!KnownDirectories.TryGetValue("AppTempDir", out string? value))
            {
                var path = Path.GetTempPath();
                path = Path.Combine(path, GetApplicationName());
                value = path;
                Directory.CreateDirectory(value);
                KnownDirectories.Add("AppTempDir", value);
            }
            return value;
        }
    }

    public static string GetModulePath()
    {
        var caller = Assembly.GetCallingAssembly();
        return caller.Location;
    }
}
