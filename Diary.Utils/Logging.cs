using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Diary.Utils;

public static class Logging
{
    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [T{ThreadId}] {SourceContext} {Message:lj}{NewLine}{Exception}";

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
            .Enrich.With(new ThreadIdEnricher())
            .WriteTo.Console(
                restrictedToMinimumLevel: minLevel,
                standardErrorFromLevel: LogEventLevel.Error,
                outputTemplate: OutputTemplate)
            .WriteTo.File(
                path: logFilePath,
                restrictedToMinimumLevel: minLevel,
                outputTemplate: OutputTemplate,
                fileSizeLimitBytes: 16 << 20,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 3)
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

    public static void Shutdown()
    {
        Log.CloseAndFlush();
        _factory?.Dispose();
        _factory = null;
        _logger = null;
    }

    private sealed class ThreadIdEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
            => logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
    }
}
