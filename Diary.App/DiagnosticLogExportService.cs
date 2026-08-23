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

        Directory.CreateDirectory(destinationDirectory);
        var exportPath = Path.Combine(
            destinationDirectory,
            $"DiaryApp-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        return ExportToArchive(sourceDirectory, exportPath);
    }

    internal static string? ExportToArchive(string sourceDirectory, string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var files = EnumerateLogFiles(sourceDirectory);
        if (files.Length == 0)
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(archivePath))!);
        if (File.Exists(archivePath))
            File.Delete(archivePath);

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
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
        return archivePath;
    }

    private static string[] EnumerateLogFiles(string sourceDirectory) =>
        Directory.Exists(sourceDirectory)
            ? Directory.EnumerateFiles(sourceDirectory, "Diary.App*.log")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : [];
}
