using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Diary.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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