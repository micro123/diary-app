using Diary.Utils;

namespace Diary.UtilTests;

[TestClass]
public class SingletonAppTests
{
    /// <summary>
    /// 同一 appId：第一个实例持锁，第二个拿不到锁 → 非首实例。
    /// 利用"同一进程内文件锁互斥"在单测内验证跨实例判据。
    /// </summary>
    [TestMethod]
    public void FirstInstanceIsSelf_SecondIsNot()
    {
        var appId = $"single_{Guid.NewGuid():N}";
        SingletonApp? first = null;
        SingletonApp? second = null;
        try
        {
            first = new SingletonApp(appId);
            Assert.IsTrue(first.IsSelfInstance(), "第一个实例应判定为自身");

            second = new SingletonApp(appId);
            Assert.IsFalse(second.IsSelfInstance(), "第二个实例应判定为非自身（锁已被占）");
        }
        finally
        {
            second?.Dispose();
            first?.Dispose();
        }
    }

    /// <summary>
    /// 不同 appId 互不干扰，各自均为首实例。
    /// </summary>
    [TestMethod]
    public void DifferentAppIds_BothSelf()
    {
        var a = new SingletonApp($"single_{Guid.NewGuid():N}");
        var b = new SingletonApp($"single_{Guid.NewGuid():N}");
        try
        {
            Assert.IsTrue(a.IsSelfInstance());
            Assert.IsTrue(b.IsSelfInstance());
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    /// <summary>
    /// Dispose 后锁释放，新实例可再次成为首实例。
    /// </summary>
    [TestMethod]
    public void DisposeReleasesLock_NextInstanceIsSelf()
    {
        var appId = $"single_{Guid.NewGuid():N}";
        var first = new SingletonApp(appId);
        Assert.IsTrue(first.IsSelfInstance());
        first.Dispose();

        // 重新创建应能再次拿到锁
        using var again = new SingletonApp(appId);
        Assert.IsTrue(again.IsSelfInstance(), "释放锁后新实例应判定为自身");
    }

    [TestMethod]
    public void DisposeRemovesLockFile()
    {
        var appId = $"single_{Guid.NewGuid():N}";
        var path = Path.Combine(FsTools.GetApplicationDataDirectory(), $"{appId}.lock");
        using (var app = new SingletonApp(appId))
        {
            Assert.IsTrue(app.IsSelfInstance());
            Assert.IsTrue(File.Exists(path));
        }

        Assert.IsFalse(File.Exists(path));
    }
}
