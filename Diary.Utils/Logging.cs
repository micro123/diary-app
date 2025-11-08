using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Diary.Utils;

public static class Logging
{
    private static ILogger? _logger;

    private static ILogger InitLogger()
    {
        var loggerFactory = LoggerFactory.Create(b =>
        {
#if DEBUG
            b.AddFilter((_) => true);
#else
            b.AddFilter((level) => level >= LogLevel.Information);
#endif
            b.AddSimpleConsole();
        });
        var result = loggerFactory.CreateLogger("Main");
        result.LogInformation("Log Initialized");
        return result;
    }

    public static ILogger Logger => _logger ??= InitLogger();
}