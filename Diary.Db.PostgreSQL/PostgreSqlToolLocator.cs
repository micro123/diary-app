namespace Diary.Db.PostgreSQL;

public sealed record PostgreSqlToolAvailability(
    bool Supported,
    string? BinDirectory,
    string? PgDumpPath,
    string? PgRestorePath,
    string? UnavailableReason);

public static class PostgreSqlToolLocator
{
    public static PostgreSqlToolAvailability Resolve(Config config)
        => Resolve(
            config,
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux(),
            Environment.GetEnvironmentVariable("PATH"));

    internal static PostgreSqlToolAvailability Resolve(
        Config config,
        bool isWindows,
        bool isLinux,
        string? pathEnvironment)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!isWindows && !isLinux)
            return Unsupported("当前操作系统不支持 PostgreSQL 应用内备份和还原。");

        var executableSuffix = isWindows ? ".exe" : string.Empty;
        if (!string.IsNullOrWhiteSpace(config.ToolsBinPath))
        {
            var configured = ResolveDirectory(config.ToolsBinPath, executableSuffix);
            if (configured.Supported)
                return configured;
            if (isWindows)
                return configured;
        }
        else if (isWindows)
        {
            return Unsupported("Windows 必须在数据库设置中配置 PostgreSQL 工具目录。");
        }

        foreach (var directory in SplitPath(pathEnvironment))
        {
            var candidate = ResolveDirectory(directory, executableSuffix);
            if (candidate.Supported)
                return candidate;
        }

        return Unsupported("未找到 pg_dump 和 pg_restore，PostgreSQL 备份和还原不可用。");
    }

    private static PostgreSqlToolAvailability ResolveDirectory(string directory, string executableSuffix)
    {
        string fullDirectory;
        try
        {
            fullDirectory = Path.GetFullPath(directory.Trim().Trim('"'));
        }
        catch (Exception exception)
        {
            return Unsupported($"PostgreSQL 工具目录无效：{exception.Message}");
        }

        var pgDumpPath = Path.Combine(fullDirectory, $"pg_dump{executableSuffix}");
        var pgRestorePath = Path.Combine(fullDirectory, $"pg_restore{executableSuffix}");
        if (!File.Exists(pgDumpPath) || !File.Exists(pgRestorePath))
        {
            return Unsupported(
                $"目录“{fullDirectory}”中未同时找到 pg_dump 和 pg_restore。");
        }

        return new PostgreSqlToolAvailability(
            true,
            fullDirectory,
            pgDumpPath,
            pgRestorePath,
            null);
    }

    private static IEnumerable<string> SplitPath(string? pathEnvironment)
        => string.IsNullOrWhiteSpace(pathEnvironment)
            ? []
            : pathEnvironment.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static PostgreSqlToolAvailability Unsupported(string reason)
        => new(false, null, null, null, reason);
}
