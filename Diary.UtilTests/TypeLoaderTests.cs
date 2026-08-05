using Diary.Utils;

namespace Diary.UtilTests;

/// <summary>
/// TypeLoader 的 zero-plugin 路径：空目录或无匹配 DLL 时返回空、不抛
/// （架构 §3.3：插件缺失不能阻止核心启动）。
/// </summary>
[TestClass]
public sealed class TypeLoaderTests
{
    [TestMethod]
    public void GetImplementations_EmptyDirectoryReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DiaryApp_TypeLoaderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = TypeLoader.GetImplementations<object>(tempDir, "*.dll");
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
