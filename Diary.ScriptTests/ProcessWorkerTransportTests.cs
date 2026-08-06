using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ProcessWorkerTransportTests
{
    [TestMethod]
    public async Task ProcessTransport_CompletesHandshakeHealthCheckAndExecution()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("当前集成测试使用 Linux dotnet 路径。");
            return;
        }

        var workerPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../Diary.Script.Worker/bin/Debug/net10.0/Diary.Script.Worker.dll"));
        Assert.IsTrue(File.Exists(workerPath), $"Worker 文件不存在：{workerPath}");
        var factory = new ProcessWorkerTransportFactory(new(
            "/usr/share/dotnet/dotnet",
            [workerPath],
            Path.GetDirectoryName(workerPath)!,
            new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" }));
        var supervisor = new WorkerSupervisor(factory);

        await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));
        Assert.IsTrue(await supervisor.CheckHealthAsync());
        var result = await supervisor.ExecuteAsync("demo", "exec-1", new { });

        Assert.AreEqual(ScriptExecutionStatus.Failed, result.Payload.Status);
        Assert.AreEqual("WORKER_EXECUTOR_NOT_CONFIGURED", result.Payload.Diagnostics.Single().Code);
        await supervisor.StopAsync();
    }

    [TestMethod]
    public async Task Factory_RejectsRelativeExecutableAndWorkingDirectory()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new ProcessWorkerTransportFactory(new("worker", [], "/tmp")).CreateAsync().AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new ProcessWorkerTransportFactory(new("/bin/sh", [], "tmp")).CreateAsync().AsTask());
    }
}
