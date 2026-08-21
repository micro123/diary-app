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

            Console.WriteLine($"{bin} {appdata} {appcfg} {temp} {module}");
            // Assert.IsTrue(true);
        }

        [TestMethod]
        public void SetApplicationRootForCurrentProcess_RejectsRelativePath()
        {
            Assert.ThrowsExactly<ArgumentException>(
                () => FsTools.SetApplicationRootForCurrentProcess("relative-profile"));
        }

        [TestMethod]
        public void SetApplicationRootForCurrentProcess_RejectsVolumeRoot()
        {
            var volumeRoot = Path.GetPathRoot(Path.GetFullPath(Environment.CurrentDirectory))!;

            Assert.ThrowsExactly<ArgumentException>(
                () => FsTools.SetApplicationRootForCurrentProcess(volumeRoot));
        }
    }
}
