using System.Text.RegularExpressions;

namespace Diary.Script.Runtime;

public static class ScriptCreationPolicy
{
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9._-]{1,63}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsValidId(string? id) => !string.IsNullOrWhiteSpace(id) && IdPattern.IsMatch(id);

    public static bool IsInsideDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullDirectory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
