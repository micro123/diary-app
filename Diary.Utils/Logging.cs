using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Diary.Utils;

public static class Logging
{
    private static ILogger? _logger;
    private static ILoggerFactory? _factory;

    private static ILoggerFactory InitLoggerFactory()
    {
        var factory = LoggerFactory.Create(b =>
        {
#if DEBUG
            b.AddFilter(_ => true);
#else
            b.AddFilter(level => level >= LogLevel.Information);
#endif
            b.AddSimpleConsole();
        });
        return factory;
    }
    
    private static ILogger InitLogger()
    {
        var result = Factory.CreateLogger("Diary.App");
        return result;
    }

    public static ILogger Logger => _logger ??= InitLogger();
    public static ILoggerFactory Factory => _factory ??=  InitLoggerFactory();
}