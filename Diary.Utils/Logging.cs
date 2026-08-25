using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Diary.Utils;

public static class Logging
{
    internal const long ApplicationLogFileSizeLimitBytes = 16L * 1024 * 1024;
    internal const int ApplicationLogRetainedFileCount = 4;

    private const string OutputTemplate =
        "[{Timestamp:MM-dd HH:mm:ss}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    private static ILogger? _logger;
    private static ILoggerFactory? _factory;

    private static ILoggerFactory InitLoggerFactory()
    {
#if DEBUG
        var minLevel = LogEventLevel.Verbose;
#else
        var minLevel = LogEventLevel.Information;
#endif
        var logFilePath = Path.Combine(FsTools.GetApplicationDataDirectory(), "Diary.App.log");
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Console(
                restrictedToMinimumLevel: minLevel,
                standardErrorFromLevel: LogEventLevel.Error,
                outputTemplate: OutputTemplate);
        Log.Logger = ConfigureFileSink(configuration, logFilePath, minLevel)
            .CreateLogger();

        var factory = LoggerFactory.Create(b =>
        {
#if DEBUG
            b.AddFilter(level => level >= LogLevel.Debug);
#else
            b.AddFilter(level => level >= LogLevel.Information);
#endif
            b.AddSerilog(dispose: true);
        });
        return factory;
    }

    internal static LoggerConfiguration ConfigureFileSink(
        LoggerConfiguration configuration,
        string logFilePath,
        LogEventLevel minLevel,
        long fileSizeLimitBytes = ApplicationLogFileSizeLimitBytes,
        int retainedFileCountLimit = ApplicationLogRetainedFileCount)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        DeleteLegacyDatedLogFiles(logFilePath);
        return configuration
            .WriteTo.File(
                path: logFilePath,
                restrictedToMinimumLevel: minLevel,
                outputTemplate: OutputTemplate,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedFileCountLimit);
    }

    private static void DeleteLegacyDatedLogFiles(string logFilePath)
    {
        var fullPath = Path.GetFullPath(logFilePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory))
            return;

        var baseName = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        foreach (var candidate in Directory.EnumerateFiles(directory, $"{baseName}*{extension}"))
        {
            var candidateBaseName = Path.GetFileNameWithoutExtension(candidate);
            var suffix = candidateBaseName.AsSpan(baseName.Length);
            var hasDate = suffix.Length >= 8 && suffix[..8].IndexOfAnyExceptInRange('0', '9') < 0;
            var hasValidRemainder = suffix.Length == 8
                || suffix.Length > 9
                && suffix[8] == '_'
                && suffix[9..].IndexOfAnyExceptInRange('0', '9') < 0;
            if (!hasDate || !hasValidRemainder)
                continue;
            try
            {
                File.Delete(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static ILogger InitLogger()
    {
        var result = Factory.CreateLogger("Diary.App");
        var isDebug = result.IsEnabled(LogLevel.Debug);
        return result;
    }

    public static ILogger Logger => _logger ??= InitLogger();
    public static ILoggerFactory Factory => _factory ??= InitLoggerFactory();

    public static void Shutdown()
    {
        Log.CloseAndFlush();
        _factory?.Dispose();
        _factory = null;
        _logger = null;
    }

}
