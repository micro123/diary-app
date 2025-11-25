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
