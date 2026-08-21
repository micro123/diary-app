using System.Runtime.InteropServices;
using System.Text;
using Diary.Utils;

namespace Diary.UtilTests;

[TestClass]
public class ProcUtilsTests
{
    /// <summary>
    /// 验证 TryStartNewInstance 真的能拉起子进程并正确转发参数。
    /// Windows 使用 Windows PowerShell、Linux 使用 /bin/sh 写标记文件，再断言文件内容。
    /// 这里测的是 Restart() 所依赖的"启动新实例"机制（不含 Environment.Exit）。
    /// </summary>
    [TestMethod]
    public void TryStartNewInstance_LaunchesChildAndForwardsArgs()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"restart_probe_{Guid.NewGuid():N}");
        if (File.Exists(marker))
            File.Delete(marker);

        try
        {
            // 参数里故意带空格，验证 ArgumentList 的逐参数转发/转义
            var content = "hello restart";
            string executable;
            string[] arguments;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                executable = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe");
                var escapedMarker = marker.Replace("'", "''", StringComparison.Ordinal);
                var script = $"[IO.File]::WriteAllText('{escapedMarker}', '{content}')";
                var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encodedCommand];
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                executable = "/bin/sh";
                arguments = ["-c", $"echo {content} > \"{marker}\""];
            }
            else
            {
                Assert.Inconclusive("当前平台没有配置重启子进程探针");
                return;
            }

            var ok = ProcUtils.TryStartNewInstance(executable, arguments);
            Assert.IsTrue(ok, "TryStartNewInstance 应返回 true");

            var probeTimeout = TimeSpan.FromSeconds(15);
            var markerCreated = SpinWait.SpinUntil(() => File.Exists(marker), probeTimeout);
            Assert.IsTrue(
                markerCreated,
                $"子进程应在 {probeTimeout.TotalSeconds:0} 秒内创建标记文件；executable={executable}");
            Assert.AreEqual(content, File.ReadAllText(marker).Trim());
        }
        finally
        {
            if (File.Exists(marker))
                File.Delete(marker);
        }
    }

    /// <summary>
    /// 不存在的可执行文件应返回 false 而非抛异常。
    /// </summary>
    [TestMethod]
    public void TryStartNewInstance_MissingTarget_ReturnsFalse()
    {
        var ok = ProcUtils.TryStartNewInstance(
            "/this/path/does/not/exist_xyz_123", Array.Empty<string>());
        Assert.IsFalse(ok, "目标不存在时应返回 false");
    }
    [TestMethod]
    public void GetRestartArguments_NativeAppHostSkipsExecutableArgument()
    {
        var appPath = Path.Combine(Path.GetTempPath(), "Diary.App.exe");
        string[] commandLineArgs = [appPath, "--core-only", "value with spaces"];

        var forwarded = ProcUtils.GetRestartArguments(appPath, commandLineArgs).ToArray();

        CollectionAssert.AreEqual(new[] { "--core-only", "value with spaces" }, forwarded);
    }

    [TestMethod]
    public void GetRestartArguments_DotnetHostKeepsManagedEntryAssembly()
    {
        var dotnetPath = Path.Combine(Path.GetTempPath(), "dotnet.exe");
        var appPath = Path.Combine(Path.GetTempPath(), "Diary.App.dll");
        string[] commandLineArgs = [appPath, "--core-only"];

        var forwarded = ProcUtils.GetRestartArguments(dotnetPath, commandLineArgs).ToArray();

        CollectionAssert.AreEqual(commandLineArgs, forwarded);
    }

}
