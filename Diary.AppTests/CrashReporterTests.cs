using System.Diagnostics;
using Diary.App.Diagnostics;

namespace Diary.AppTests;

[TestClass]
[DoNotParallelize]
public sealed class CrashReporterTests
{
    [TestMethod]
    public void Store_RoundTripsRequestAndResultAndPrunesOldDumps()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var occurredAt = new DateTimeOffset(2026, 8, 16, 10, 20, 30, TimeSpan.Zero);
            var (request, requestPath) = CrashReportStore.CreateRequest(
                new InvalidOperationException("boom"),
                directory,
                processId: 123,
                processName: "Diary:App",
                occurredAtUtc: occurredAt);

            Assert.IsTrue(File.Exists(requestPath));
            Assert.AreEqual(123, request.ProcessId);
            Assert.AreEqual("boom", request.ExceptionMessage);
            var loadedRequest = CrashReportStore.ReadRequest(requestPath);
            Assert.AreEqual(request, loadedRequest);

            File.WriteAllBytes(request.DumpPath, [1, 2, 3]);
            var result = new CrashReportResult(request, true, 3, null);
            CrashReportStore.WriteResult(result);
            Assert.AreEqual(result, CrashReportStore.ReadResult(request.ResultPath));

            for (var index = 0; index < 6; index++)
            {
                var path = Path.Combine(directory, $"old-{index}.dmp");
                File.WriteAllText(path, index.ToString());
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-index - 1));
            }
            CrashReportStore.Prune(directory, maxDumpCount: 3);

            Assert.AreEqual(3, Directory.EnumerateFiles(directory, "*.dmp").Count());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task CaptureProcess_CreatesTriageDumpForLiveDotnetWorker()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("CrashDump 集成测试仅覆盖 Windows/Linux。");
            return;
        }
        var directory = CreateTemporaryDirectory();
        using var process = StartWorkerProcess();
        try
        {
            await Task.Delay(500);
            Assert.IsFalse(process.HasExited, "Worker 在 Dump 捕获前意外退出。");
            var (request, requestPath) = CrashReportStore.CreateRequest(
                new InvalidOperationException("integration crash"),
                directory,
                process.Id,
                process.ProcessName,
                showDialog: false);
            using var captureProcess = StartCrashCaptureProcess(requestPath);

            await captureProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

            Assert.AreEqual(0, captureProcess.ExitCode);
            var result = CrashReportStore.ReadResult(request.ResultPath);
            Assert.IsTrue(result.DumpSucceeded, result.ErrorMessage);
            Assert.IsTrue(File.Exists(request.DumpPath));
            Assert.IsGreaterThan(0, new FileInfo(request.DumpPath).Length);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public void SelfProcessArguments_IncludeCrashModeAndRequestPath()
    {
        var startInfo = CrashReporterProcess.CreateSelfStartInfo(
            [CrashReporterProcess.CaptureArgument, "report.json"],
            createNoWindow: true);

        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), CrashReporterProcess.CaptureArgument);
        CollectionAssert.Contains(startInfo.ArgumentList.ToArray(), "report.json");
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.CreateNoWindow);
    }


    private static Process StartCrashCaptureProcess(string requestPath)
    {
        var executableName = OperatingSystem.IsWindows() ? "Diary.App.exe" : "Diary.App";
        var executablePath = Path.Combine(AppContext.BaseDirectory, executableName);
        Assert.IsTrue(File.Exists(executablePath), $"Diary.App apphost 不存在：{executablePath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(CrashReporterProcess.CaptureArgument);
        startInfo.ArgumentList.Add(requestPath);
        return Process.Start(startInfo)
            ?? throw new AssertFailedException("无法启动崩溃捕获进程。");
    }

    private static Process StartWorkerProcess()
    {
        var workerPath = GetWorkerPath();
        Assert.IsTrue(File.Exists(workerPath), $"Worker 文件不存在：{workerPath}");
        var startInfo = new ProcessStartInfo
        {
            FileName = GetDotnetPath(),
            WorkingDirectory = Path.GetDirectoryName(workerPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(workerPath);
        startInfo.ArgumentList.Add("--language");
        startInfo.ArgumentList.Add("csharp");
        return Process.Start(startInfo)
            ?? throw new AssertFailedException("无法启动测试 Worker 进程。");
    }

    private static string GetWorkerPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name;
        if (configuration is not ("Debug" or "Release"))
            configuration = "Release";
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../Diary.Script.Worker/bin",
            configuration,
            "net10.0/Diary.Script.Worker.dll"));
    }

    private static string GetDotnetPath()
    {
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? string.Empty, executableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", executableName),
            "/usr/share/dotnet/dotnet",
        };
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => Path.Combine(directory, executableName)));
        }
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => Path.GetFullPath(candidate!))
            .FirstOrDefault(File.Exists)
            ?? throw new AssertFailedException("找不到可用的 dotnet 可执行文件。");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"diary-crash-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
    }
}
