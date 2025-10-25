using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.UtilTests;

[TestClass]
public class LoggingTests
{
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