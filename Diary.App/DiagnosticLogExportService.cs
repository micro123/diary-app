using System.IO.Compression;
using Diary.Utils;

namespace Diary.App;

[DiAutoRegister(singleton: true)]
public sealed class DiagnosticLogExportService
{
    public string? GetCurrentLogFile()
        => FindCurrentLogFile(FsTools.GetApplicationDataDirectory());

    public string? Export()
        => Export(FsTools.GetApplicationDataDirectory(), FsTools.GetTemporaryDirectory());

    public static string? FindCurrentLogFile(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        if (!Directory.Exists(sourceDirectory))
            return null;

        return Directory.EnumerateFiles(sourceDirectory, "Diary.App*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static string? Export(string sourceDirectory, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var files = Directory.Exists(sourceDirectory)
            ? Directory.EnumerateFiles(sourceDirectory, "Diary.App*.log")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();
        if (files.Length == 0)
            return null;

        Directory.CreateDirectory(destinationDirectory);
        var exportPath = Path.Combine(
            destinationDirectory,
            $"DiaryApp-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (File.Exists(exportPath))
            File.Delete(exportPath);

        using var archive = ZipFile.Open(exportPath, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Fastest);
            using var source = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var target = entry.Open();
            source.CopyTo(target);
        }
        return exportPath;
    }
}
