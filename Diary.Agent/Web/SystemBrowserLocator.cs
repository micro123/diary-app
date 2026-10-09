namespace Diary.Agent.Web;

public sealed class SystemBrowserLocator
{
    private static readonly string[] WindowsCandidates =
    [
        @"Microsoft\Edge\Application\msedge.exe",
        @"Google\Chrome\Application\chrome.exe",
        @"Chromium\Application\chrome.exe",
    ];

    private static readonly string[] UnixCommands =
    [
        "microsoft-edge",
        "microsoft-edge-stable",
        "google-chrome",
        "google-chrome-stable",
        "chromium",
        "chromium-browser",
    ];

    public string? Find(BrowserAccessPolicy policy)
    {
        if (policy.Mode == BrowserAccessMode.Executable)
            return NormalizeExistingFile(policy.ExecutablePath);
        if (policy.Mode != BrowserAccessMode.System)
            return null;
        if (OperatingSystem.IsWindows())
            return FindWindowsBrowser();
        if (OperatingSystem.IsMacOS())
            return FindMacBrowser();
        return FindOnPath(UnixCommands);
    }

    private static string? FindWindowsBrowser()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var root in roots.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            foreach (var relativePath in WindowsCandidates)
            {
                var result = NormalizeExistingFile(Path.Combine(root, relativePath));
                if (result is not null)
                    return result;
            }
        }
        return FindOnPath(["msedge.exe", "chrome.exe", "chromium.exe"]);
    }

    private static string? FindMacBrowser()
    {
        string[] candidates =
        [
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
            "/Applications/Chromium.app/Contents/MacOS/Chromium",
        ];
        return candidates.Select(NormalizeExistingFile).FirstOrDefault(value => value is not null)
               ?? FindOnPath(UnixCommands);
    }

    private static string? FindOnPath(IEnumerable<string> commands)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var command in commands)
            {
                var result = NormalizeExistingFile(Path.Combine(directory, command));
                if (result is not null)
                    return result;
            }
        }
        return null;
    }

    private static string? NormalizeExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
