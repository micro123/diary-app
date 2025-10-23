using System.Reflection;
using System.IO;

namespace Diary.Utils;

public static class FsTools
{
    private static readonly Dictionary<string, string> _knownDirectories = new();

    public static string GetBinaryDirectory()
    {
        lock (_knownDirectories)
        {
            if (!_knownDirectories.TryGetValue("AppBinDir", out string? value))
            {
                var assembly = Assembly.GetEntryAssembly();
                var path = assembly!.Location;
                value = Path.GetDirectoryName(path)!;
                _knownDirectories.Add("AppBinDir", value);
            }
            return value;
        }
    }

    private static string GetApplicationName()
    {
        if (!_knownDirectories.TryGetValue("AppName", out string? value))
        {
            var assembly = Assembly.GetEntryAssembly();
            var name = Path.GetFileNameWithoutExtension(assembly!.Location);
            value = name;
            Directory.CreateDirectory(value);
            _knownDirectories.Add("AppName", value);
        }
        return value;
    }

    public static string GetApplicationConfigDirectory()
    {
        lock (_knownDirectories)
        {
            if (!_knownDirectories.TryGetValue("AppCfgDir", out string? value))
            {
                var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                appdata = Path.Combine(appdata, GetApplicationName());
                value = appdata;
                Directory.CreateDirectory(value);
                _knownDirectories.Add("AppCfgDir", value);
            }
            return value;
        }
    }

    public static string GetApplicationDataDirectory()
    {
        lock (_knownDirectories)
        {
            if (!_knownDirectories.TryGetValue("AppDataDir", out string? value))
            {
                var appdata = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                appdata = Path.Combine(appdata, GetApplicationName());
                value = appdata;
                Directory.CreateDirectory(value);
                _knownDirectories.Add("AppDataDir", value);
            }
            return value;
        }
    }


    public static string GetTemporaryDirectory()
    {
        lock (_knownDirectories)
        {
            if (!_knownDirectories.TryGetValue("AppTempDir", out string? value))
            {
                var path = Path.GetTempPath();
                path = Path.Combine(path, GetApplicationName());
                value = path;
                Directory.CreateDirectory(value);
                _knownDirectories.Add("AppTempDir", value);
            }
            return value;
        }
    }

    public static string GetModulePath()
    {
        Assembly caller = Assembly.GetCallingAssembly();
        return caller.Location!;
    }
}
