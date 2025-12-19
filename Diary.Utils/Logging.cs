using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Diary.Utils;

public static class Logging
{
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
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Console(restrictedToMinimumLevel: minLevel, standardErrorFromLevel: LogEventLevel.Error)
            .WriteTo.File(path: logFilePath, minLevel, fileSizeLimitBytes: 16 << 20, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 3)
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

    private static ILogger InitLogger()
    {
        var result = Factory.CreateLogger("Diary.App");
        var isDebug = result.IsEnabled(LogLevel.Debug);
        return result;
    }

    public static ILogger Logger => _logger ??= InitLogger();
    public static ILoggerFactory Factory => _factory ??= InitLoggerFactory();
}