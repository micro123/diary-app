using Microsoft.Diagnostics.NETCore.Client;

namespace Diary.App.Diagnostics;

internal static class CrashDumpCaptureService
{
    public static CrashReportResult Capture(CrashReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(request.DumpDirectory);
        try
        {
            var client = new DiagnosticsClient(request.ProcessId);
            client.WriteDump(DumpType.Triage, request.DumpPath, logDumpGeneration: false);
            var dump = new FileInfo(request.DumpPath);
            if (!dump.Exists || dump.Length == 0)
                throw new IOException("诊断服务没有生成有效的 Dump 文件。");
            return new CrashReportResult(request, true, dump.Length, null);
        }
        catch (Exception exception)
        {
            return new CrashReportResult(
                request,
                false,
                null,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }
}
