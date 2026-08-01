using System.Runtime.InteropServices;
using Diary.Utils;

namespace Diary.UtilTests;

[TestClass]
public class ProcUtilsTests
{
    /// <summary>
    /// 验证 TryStartNewInstance 真的能拉起子进程并正确转发参数。
    /// 用 /bin/sh 写一个标记文件，再断言文件被写出、内容正确。
    /// 这里测的是 Restart() 所依赖的"启动新实例"机制（不含 Environment.Exit）。
    /// </summary>
    [TestMethod]
    public void TryStartNewInstance_LaunchesChildAndForwardsArgs()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Inconclusive("本用例仅验证了 Linux 下的 /bin/sh 调用");
            return;
        }

        var marker = Path.Combine(Path.GetTempPath(), $"restart_probe_{Guid.NewGuid():N}");
        if (File.Exists(marker))
            File.Delete(marker);

        try
        {
            // 参数里故意带空格，验证 ArgumentList 的逐参数转发/转义
            var content = "hello restart";
            var script = $"echo {content} > \"{marker}\"";

            var ok = ProcUtils.TryStartNewInstance("/bin/sh", new[] { "-c", script });
            Assert.IsTrue(ok, "TryStartNewInstance 应返回 true");

            // 等待子进程写出标记文件（最多 2 秒）
            for (var i = 0; i < 40 && !File.Exists(marker); i++)
                Thread.Sleep(50);

            Assert.IsTrue(File.Exists(marker), "子进程应已创建标记文件");
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
}
