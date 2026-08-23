using Microsoft.Diagnostics.NETCore.Client;

namespace Diary.App.Diagnostics;

internal static class CrashDumpCaptureService
{
    public static CrashReportResult Capture(CrashReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(request.DumpDirectory);
        var dumpSucceeded = false;
        long? dumpSizeBytes = null;
        string? dumpErrorMessage = null;
        try
        {
            var client = new DiagnosticsClient(request.ProcessId);
            client.WriteDump(DumpType.Triage, request.DumpPath, logDumpGeneration: false);
            var dump = new FileInfo(request.DumpPath);
            if (!dump.Exists || dump.Length == 0)
                throw new IOException("诊断服务没有生成有效的 Dump 文件。");
            dumpSucceeded = true;
            dumpSizeBytes = dump.Length;
        }
        catch (Exception exception)
        {
            dumpErrorMessage = $"{exception.GetType().Name}: {exception.Message}";
        }

        var (logArchiveSucceeded, logArchiveSizeBytes, logArchiveErrorMessage) = CaptureLogs(request);
        return new CrashReportResult(
            request,
            dumpSucceeded,
            dumpSizeBytes,
            dumpErrorMessage,
            logArchiveSucceeded,
            logArchiveSizeBytes,
            logArchiveErrorMessage);
    }

    private static (bool Succeeded, long? SizeBytes, string? ErrorMessage) CaptureLogs(
        CrashReportRequest request)
    {
        try
        {
            var archivePath = DiagnosticLogExportService.ExportToArchive(
                request.LogDirectory,
                request.LogArchivePath);
            if (archivePath is null)
                return (false, null, "没有找到可收集的应用日志。");

            var archive = new FileInfo(archivePath);
            if (!archive.Exists || archive.Length == 0)
                throw new IOException("日志归档文件为空。");
            return (true, archive.Length, null);
        }
        catch (Exception exception)
        {
            return (false, null, $"{exception.GetType().Name}: {exception.Message}");
        }
    }
}
