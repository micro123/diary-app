namespace Diary.Update;

public static class UpdatePathPolicy
{
    public static string NormalizeAbsolute(string path, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"{fieldName} 必须是绝对路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string NormalizeRelative(string path, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException($"{fieldName} 不能为空。");
        if (path.Contains('\\', StringComparison.Ordinal) || Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"{fieldName} 必须使用 / 分隔的相对路径。");
        var parts = path.Split('/', StringSplitOptions.None);
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.Contains(':', StringComparison.Ordinal)))
            throw new InvalidDataException($"{fieldName} 包含非法路径段。");
        return string.Join('/', parts);
    }

    public static string ResolveInside(string root, string relativePath, string fieldName)
    {
        var normalizedRoot = NormalizeAbsolute(root, nameof(root));
        var normalizedRelative = NormalizeRelative(relativePath, fieldName);
        var candidate = Path.GetFullPath(Path.Combine(normalizedRoot, normalizedRelative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureInside(normalizedRoot, candidate, fieldName);
        return candidate;
    }

    public static void EnsureInside(string root, string candidate, string fieldName)
    {
        var normalizedRoot = NormalizeAbsolute(root, nameof(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(prefix, comparison))
            throw new InvalidDataException($"{fieldName} 必须位于允许的根目录内。");
    }

    public static bool Overlaps(string left, string right)
    {
        var normalizedLeft = NormalizeAbsolute(left, nameof(left));
        var normalizedRight = NormalizeAbsolute(right, nameof(right));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(normalizedLeft, normalizedRight, comparison)
               || normalizedLeft.StartsWith(normalizedRight + Path.DirectorySeparatorChar, comparison)
               || normalizedRight.StartsWith(normalizedLeft + Path.DirectorySeparatorChar, comparison);
    }

    public static void RejectExistingLinks(string root, string candidate, string fieldName)
    {
        var normalizedRoot = NormalizeAbsolute(root, nameof(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        EnsureInside(normalizedRoot, normalizedCandidate, fieldName);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        var current = normalizedRoot;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileSystemInfo? info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;
            if (info is null)
                continue;
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"{fieldName} 不能经过链接或重解析点：{current}");
        }
    }
}
