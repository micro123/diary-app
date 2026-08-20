using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.UtilTests;

[TestClass]
public class LoggingTests
{
    [TestMethod]
    public void LogIncludesManagedThreadId()
    {
        var logDirectory = FsTools.GetApplicationDataDirectory();
        var threadId = Environment.CurrentManagedThreadId;

        Logging.Logger.LogInformation("线程 ID 测试 {ThreadId}", threadId);
        Logging.Shutdown();

        var logPath = Directory.EnumerateFiles(logDirectory, "Diary.App*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
        var log = File.ReadAllText(logPath);
        StringAssert.Contains(log, $"[T{threadId}]");
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
}
