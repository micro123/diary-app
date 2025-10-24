using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.UtilTests;
[TestClass]
public class SysInfoTests
{
    [TestMethod]
    public void MachineName()
    {
        var name = SysInfo.GetHostname();
        Console.WriteLine(name);
    }

    [TestMethod]
    public void UserName()
    {
        var name = SysInfo.GetUsername();
        Console.WriteLine(name);
    }
}