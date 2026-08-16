using System.Diagnostics;
using System.Reflection;
using Avalonia;

namespace Diary.App.Diagnostics;

internal static class CrashReporterProcess
{
    internal const string CaptureArgument = "--capture-crash-dump";
    internal const string ShowArgument = "--show-crash-report";
    private static readonly TimeSpan CaptureWaitTimeout = TimeSpan.FromSeconds(30);
    private static int _captureStarted;

    public static bool TryRun(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], CaptureArgument, StringComparison.Ordinal))
        {
            Environment.ExitCode = Capture(args[1]);
            return true;
        }
        if (args.Length == 2 && string.Equals(args[0], ShowArgument, StringComparison.Ordinal))
        {
            Environment.ExitCode = Show(args[1]);
            return true;
        }
        return false;
    }

    public static void InstallUnhandledExceptionCapture() =>
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

    internal static ProcessStartInfo CreateSelfStartInfo(
        IReadOnlyList<string> arguments,
        bool createNoWindow)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序的可执行文件路径。");
        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = createNoWindow,
        };
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            startInfo.ArgumentList.Add(entryAssemblyPath);
        }
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (!args.IsTerminating
            || args.ExceptionObject is not Exception exception
            || Interlocked.Exchange(ref _captureStarted, 1) != 0)
        {
            return;
        }
        try
        {
            var (_, requestPath) = CrashReportStore.CreateRequest(exception);
            using var process = Process.Start(CreateSelfStartInfo(
                [CaptureArgument, requestPath],
                createNoWindow: true));
            process?.WaitForExit(CaptureWaitTimeout);
        }
        catch
        {
            // 致命异常处理必须保持 best-effort，不能覆盖原始崩溃。
        }
    }

    private static int Capture(string requestPath)
    {
        CrashReportResult result;
        try
        {
            var request = CrashReportStore.ReadRequest(requestPath);
            result = CrashDumpCaptureService.Capture(request);
            CrashReportStore.WriteResult(result);
            CrashReportStore.Prune(request.DumpDirectory);
        }
        catch (Exception exception)
        {
            return WriteCaptureFailure(requestPath, exception);
        }
        finally
        {
            CrashReportStore.DeleteRequest(requestPath);
        }
        if (result.Request.ShowDialog)
            TryStartShowProcess(result.Request.ResultPath);
        return result.DumpSucceeded ? 0 : 1;
    }

    private static int Show(string resultPath)
    {
        try
        {
            var result = CrashReportStore.ReadResult(resultPath);
            return AppBuilder.Configure(() => new CrashReporterApplication(result))
                .UsePlatformDetect()
                .LogToTrace()
                .StartWithClassicDesktopLifetime([]);
        }
        catch
        {
            return 1;
        }
    }

    private static int WriteCaptureFailure(string requestPath, Exception exception)
    {
        try
        {
            var request = CrashReportStore.ReadRequest(requestPath);
            var result = new CrashReportResult(
                request,
                false,
                null,
                $"{exception.GetType().Name}: {exception.Message}");
            CrashReportStore.WriteResult(result);
            if (request.ShowDialog)
                TryStartShowProcess(request.ResultPath);
        }
        catch
        {
        }
        return 1;
    }

    private static void TryStartShowProcess(string resultPath)
    {
        try
        {
            _ = Process.Start(CreateSelfStartInfo(
                [ShowArgument, resultPath],
                createNoWindow: false));
        }
        catch
        {
        }
    }
}
