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
        var result = await supervisor.ExecuteAsync("demo", "exec-1", new
        {
            ScriptId = "demo",
            SourcePath = "demo.cs",
            Source = "public sealed class Demo : Diary.ScriptBase.IScriptProgramV1 { public Diary.ScriptBase.ScriptDescriptor Descriptor { get; } = new(\"demo\", \"Demo\", Diary.ScriptBase.ScriptApiVersion.V1, Diary.ScriptBase.ScriptScope.Application, Diary.ScriptBase.ScriptCapability.None); public System.Threading.Tasks.ValueTask<Diary.ScriptBase.ScriptExecutionResult> ExecuteAsync(Diary.ScriptBase.ScriptExecutionRequest request, Diary.ScriptBase.IScriptExecutionContext context, System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.ValueTask.FromResult(Diary.ScriptBase.ScriptExecutionResult.Succeeded()); }",
            Request = new ScriptExecutionRequest(new ScriptTarget(ScriptScope.Application), Source: ScriptExecutionSource.Manual),
        });

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status,
            string.Join("; ", result.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        Assert.IsFalse(string.IsNullOrWhiteSpace(supervisor.WorkerId));
        await supervisor.StopAsync();
    }

    [TestMethod]
    public async Task ProcessTransport_LuaWorkerExecutesScript()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("当前集成测试使用 Linux dotnet 路径。");
            return;
        }

        var workerPath = GetWorkerPath();
        var supervisor = CreateDotnetSupervisor(workerPath, "lua");
        try
        {
            await supervisor.StartAsync(new("lua", [ScriptApiVersion.V1], ["workItems.query"]));
            var result = await supervisor.ExecuteAsync("lua-demo", "lua-exec-1", new WorkerExecutePayload(
                "lua-demo",
                "demo.lua",
                "function main(context) return nil end",
                new ScriptExecutionRequest(new ScriptTarget(ScriptScope.Application), Source: ScriptExecutionSource.Manual),
                new ScriptDescriptorHint("lua-demo", "Lua Demo", ScriptScope.Application, ScriptCapability.None, EngineName: "lua")));

            Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status,
                string.Join("; ", result.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        }
        finally
        {
            await supervisor.StopAsync();
        }
    }

    [TestMethod]
    public async Task ProcessTransport_PythonWorkerExecutesScriptAndIsolatesPrint()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("当前集成测试使用 Linux Python 路径。");
            return;
        }

        var runtime = await new Diary.Script.Py.PythonRuntimeResolver().ResolveAsync();
        if (!runtime.Succeeded || runtime.ExecutablePath is null)
        {
            Assert.Inconclusive("当前环境没有可用的 Python 3.10+ runtime。");
            return;
        }

        var supervisor = new WorkerSupervisor(new ProcessWorkerTransportFactory(new WorkerProcessOptions(
            runtime.ExecutablePath,
            Diary.Script.Py.PythonWorkerSource.CreateArguments(),
            AppContext.BaseDirectory,
            new Dictionary<string, string>
            {
                ["PYTHONIOENCODING"] = "utf-8",
                ["PYTHONUNBUFFERED"] = "1",
            })));
        try
        {
            await supervisor.StartAsync(new("python", [ScriptApiVersion.V1], ["workItems.query"]));
            var result = await supervisor.ExecuteAsync("python-demo", "python-exec-1", new WorkerExecutePayload(
                "python-demo",
                "demo.py",
                "def main(context):\n    print(\"not protocol\")\n    return None\n",
                new ScriptExecutionRequest(new ScriptTarget(ScriptScope.Application), Source: ScriptExecutionSource.Manual),
                new ScriptDescriptorHint("python-demo", "Python Demo", ScriptScope.Application, ScriptCapability.None, EngineName: "python")));

            Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status,
                string.Join("; ", result.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        }
        finally
        {
            await supervisor.StopAsync();
        }
    }

    [TestMethod]
    public async Task ProcessTransport_ReportsExitCodeWhenProcessTerminates()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("当前集成测试使用 Linux shell 退出码。");
            return;
        }

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", "exit 7" },
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        await process.WaitForExitAsync();
        await using var transport = new ProcessWorkerTransport(process);

        Assert.AreEqual(7, ((IWorkerTerminationNotification)transport).ExitCode);
    }

    [TestMethod]
    public async Task ProcessTransport_StopKillsProcessAfterGracePeriod()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("当前集成测试使用 Linux shell 进程。");
            return;
        }

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", "trap '' TERM; sleep 30" },
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        process.Start();
        await using var transport = new ProcessWorkerTransport(
            process,
            shutdownGracePeriod: TimeSpan.FromMilliseconds(50));

        await transport.StopAsync();

        Assert.IsTrue(process.HasExited);
    }

    [TestMethod]
    public async Task Factory_RejectsRelativeExecutableAndWorkingDirectory()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new ProcessWorkerTransportFactory(new("worker", [], "/tmp")).CreateAsync().AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new ProcessWorkerTransportFactory(new("/bin/sh", [], "tmp")).CreateAsync().AsTask());
    }

    private static WorkerSupervisor CreateDotnetSupervisor(string workerPath, string language) =>
        new(new ProcessWorkerTransportFactory(new(
            "/usr/share/dotnet/dotnet",
            [workerPath, "--language", language],
            Path.GetDirectoryName(workerPath)!,
            new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" })));

    private static string GetWorkerPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../Diary.Script.Worker/bin/Debug/net10.0/Diary.Script.Worker.dll"));
}
