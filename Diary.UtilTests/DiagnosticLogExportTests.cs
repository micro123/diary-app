using System.IO.Compression;
using Diary.App;

namespace Diary.UtilTests;

[TestClass]
public sealed class DiagnosticLogExportTests
{
    [TestMethod]
    public void ExportCopiesAllApplicationLogsToZip()
    {
        var source = CreateDirectory();
        var destination = CreateDirectory();
        try
        {
            File.WriteAllText(Path.Combine(source, "Diary.App.log"), "current");
            File.WriteAllText(Path.Combine(source, "Diary.App20260805.log"), "previous");
            File.WriteAllText(Path.Combine(source, "other.txt"), "ignored");

            var export = DiagnosticLogExportService.Export(source, destination);

            Assert.IsNotNull(export);
            using var archive = ZipFile.OpenRead(export);
            CollectionAssert.AreEquivalent(
                new[] { "Diary.App.log", "Diary.App20260805.log" },
                archive.Entries.Select(entry => entry.Name).ToArray());
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(destination, recursive: true);
        }
    }

    [TestMethod]
    public void FindCurrentLogFileReturnsMostRecentlyWrittenApplicationLog()
    {
        var source = CreateDirectory();
        try
        {
            var previous = Path.Combine(source, "Diary.App20260816.log");
            var current = Path.Combine(source, "Diary.App20260817.log");
            File.WriteAllText(previous, "previous");
            File.WriteAllText(current, "current");
            File.WriteAllText(Path.Combine(source, "other.log"), "ignored");
            File.SetLastWriteTimeUtc(previous, new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(current, new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc));

            Assert.AreEqual(current, DiagnosticLogExportService.FindCurrentLogFile(source));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [TestMethod]
    public void FindCurrentLogFileWithoutLogsReturnsNull()
    {
        var source = CreateDirectory();
        try
        {
            Assert.IsNull(DiagnosticLogExportService.FindCurrentLogFile(source));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [TestMethod]
    public void ExportWithoutLogsReturnsNull()
    {
        var source = CreateDirectory();
        var destination = CreateDirectory();
        try
        {
            Assert.IsNull(DiagnosticLogExportService.Export(source, destination));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            Directory.Delete(destination, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DiaryApp_LogExportTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
