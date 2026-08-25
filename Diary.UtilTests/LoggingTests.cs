using Diary.Utils;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

using System.Text.RegularExpressions;
namespace Diary.UtilTests;

[TestClass]
public class LoggingTests
{
    [TestMethod]
    public void FileSinkUsesCompactTimestampAndOmitsThreadMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"diary-log-format-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Diary.App.log");
            using (var logger = Logging.ConfigureFileSink(
                       new LoggerConfiguration().MinimumLevel.Verbose(),
                       path,
                       LogEventLevel.Verbose)
                   .CreateLogger())
            {
                logger.ForContext<LoggingTests>().Information("紧凑格式");
            }

            var log = File.ReadAllText(path);
            Assert.IsTrue(
                Regex.IsMatch(
                    log,
                    @"^\[\d{2}-\d{2} \d{2}:\d{2}:\d{2}\] \[INF\] \[Diary\.UtilTests\.LoggingTests\] 紧凑格式\r?$",
                    RegexOptions.Multiline),
                log);
            Assert.DoesNotContain("[T", log);
            Assert.IsFalse(Regex.IsMatch(log, @"\.\d{3} [+-]\d{2}:\d{2}\]"), log);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void LogSimple()
    {
        var log = Logging.Logger;
        log.LogTrace("你好");
        log.LogDebug("你好");
        log.LogInformation("你好");
        log.LogWarning("你好");
        log.LogError("你好");
    }

    [TestMethod]
    public void FileSinkRollsAtSizeLimitAndRetainsConfiguredFileCount()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"diary-log-roll-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Diary.App.log");
            File.WriteAllText(Path.Combine(directory, "Diary.App20260821.log"), "legacy");
            File.WriteAllText(Path.Combine(directory, "Diary.App20260822.log"), "legacy");
            using (var logger = Logging.ConfigureFileSink(
                       new LoggerConfiguration().MinimumLevel.Verbose(),
                       path,
                       LogEventLevel.Verbose,
                       fileSizeLimitBytes: 1024,
                       retainedFileCountLimit: 4)
                   .CreateLogger())
            {
                var payload = new string('x', 700);
                for (var index = 0; index < 12; index++)
                    logger.Information("滚动日志测试 {Index} {Payload}", index, payload);
            }

            var files = Directory.EnumerateFiles(directory, "Diary.App*.log").ToArray();
            Assert.HasCount(4, files);
            CollectionAssert.AreEquivalent(
                new[] { "Diary.App_002.log", "Diary.App_003.log", "Diary.App_004.log", "Diary.App_005.log" },
                files.Select(Path.GetFileName).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
