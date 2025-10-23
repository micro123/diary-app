using Diary.Utils;

namespace Diary.UtilTests
{
    [TestClass]
    public sealed class FsTests
    {
        [TestMethod]
        public void GetPaths()
        {
            var bin = FsTools.GetBinaryDirectory();
            var appdata = FsTools.GetApplicationDataDirectory();
            var appcfg = FsTools.GetApplicationConfigDirectory();
            var temp = FsTools.GetTemporaryDirectory();
            var module = FsTools.GetModulePath();

            Console.Write($"{bin} {appdata} {appcfg} {temp} {module}");
            // Assert.IsTrue(true);
        }
    }
}
