using Microsoft.Extensions.Logging;

namespace Diary.Utils;

public static class Logging
{
    private static ILogger? _logger;

    private static ILogger InitLogger()
    {
        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddConsole();
        });
        var result = loggerFactory.CreateLogger("Program");
        result.LogInformation("Log Initialized");
        return result;
    }

    public static ILogger Logger => _logger ??= InitLogger();
}