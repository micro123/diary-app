using System.IO.Compression;
using Diary.App;

namespace Diary.AppTests;

[TestClass]
public sealed class DiagnosticLogExportServiceTests
{
    [TestMethod]
    public void Export_ReadsCurrentLogWhileWriterKeepsFileOpen()
    {
        var root = CreateRoot();
        var sourceDirectory = Path.Combine(root, "data");
        var destinationDirectory = Path.Combine(root, "temp");
        Directory.CreateDirectory(sourceDirectory);
        var logPath = Path.Combine(sourceDirectory, "Diary.App20260821.log");
        const string content = "active log content";
        File.WriteAllText(logPath, content);

        try
        {
            using (new FileStream(logPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                var result = DiagnosticLogExportService.Export(sourceDirectory, destinationDirectory);

                Assert.IsNotNull(result);
                Assert.IsTrue(File.Exists(result));
                using var archive = ZipFile.OpenRead(result);
                var entry = archive.GetEntry(Path.GetFileName(logPath));
                Assert.IsNotNull(entry);
                using var reader = new StreamReader(entry.Open());
                Assert.AreEqual(content, reader.ReadToEnd());
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void Export_ReturnsNullWhenNoLogExists()
    {
        var root = CreateRoot();
        try
        {
            Assert.IsNull(DiagnosticLogExportService.Export(root, Path.Combine(root, "temp")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"diary-log-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
