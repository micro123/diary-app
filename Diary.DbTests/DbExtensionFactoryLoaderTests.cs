using Diary.Database;

namespace Diary.DbTests;

[TestClass]
public sealed class DbExtensionFactoryLoaderTests
{
    [TestMethod]
    public void LoadFromDirectory_ReportsBrokenOptionalAssemblyAndContinues()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"diary-db-extension-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Diary.Broken.dll");
        File.WriteAllText(path, "not an assembly");
        var failures = new List<(string Source, Exception Exception)>();

        try
        {
            var factories = DbExtensionFactoryLoader.LoadFromDirectory(
                directory,
                (source, exception) => failures.Add((source, exception)));

            Assert.AreEqual(0, factories.Count);
            Assert.AreEqual(1, failures.Count);
            Assert.AreEqual(path, failures[0].Source);
            Assert.IsInstanceOfType<BadImageFormatException>(failures[0].Exception);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
