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
            Source = "public sealed class Demo : Diary.ScriptBase.IScriptProgramV1 { public Diary.ScriptBase.ScriptDescriptor Descriptor { get; } = new(\"demo\", \"Demo\", Diary.ScriptBase.ScriptApiVersion.V1, Diary.ScriptBase.ScriptScope.Application); public System.Threading.Tasks.ValueTask<Diary.ScriptBase.ScriptExecutionResult> ExecuteAsync(Diary.ScriptBase.ScriptExecutionRequest request, Diary.ScriptBase.IScriptExecutionContext context, System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.ValueTask.FromResult(Diary.ScriptBase.ScriptExecutionResult.Succeeded()); }",
            Request = new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
        });

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status,
            string.Join("; ", result.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        Assert.IsFalse(string.IsNullOrWhiteSpace(supervisor.WorkerId));
        await supervisor.StopAsync();
    }

    [TestMethod]
    public async Task ProcessTransport_HeartbeatKeepsWorkerAlive()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("当前集成测试使用 Linux dotnet 路径。");
            return;
        }

        var workerPath = GetWorkerPath();
        var factory = new ProcessWorkerTransportFactory(new(
            GetDotnetPath() ?? throw new InvalidOperationException("dotnet 路径不可用。"),
            [workerPath, "--language", "csharp"],
            Path.GetDirectoryName(workerPath)!,
            new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" }));
        var supervisor = new WorkerSupervisor(
            factory,
            cancellationGracePeriod: TimeSpan.FromSeconds(2),
            heartbeatInterval: TimeSpan.FromMilliseconds(300),
            heartbeatTimeout: TimeSpan.FromSeconds(5),
            resourceCheckInterval: TimeSpan.FromMilliseconds(100));
        try
        {
            await supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], []));

            await Task.Delay(800);

            Assert.AreEqual(WorkerState.Ready, supervisor.State);
        }
        finally
        {
            await supervisor.StopAsync();
        }
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
                "function application_main(context) return nil end",
                 new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                 new ScriptDescriptorHint("lua-demo", "Lua Demo", ScriptScope.Application, EngineName: "lua")));

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
                "def application_main(context):\n    print(\"not protocol\")\n    value = next(value for value in [1])\n    print(value)\n    return None\n",
                 new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                 new ScriptDescriptorHint("python-demo", "Python Demo", ScriptScope.Application, EngineName: "python")));

            Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status,
                string.Join("; ", result.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
        }
        finally
        {
            await supervisor.StopAsync();
        }
    }

    [TestMethod]
    public async Task WorkerScriptExecutor_RoutesLuaAndPythonLikeApplication()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("当前集成测试使用 Linux Worker 产物。");
            return;
        }

        var workerPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../Diary.App/bin/Debug/net10.0/Diary.Script.Worker"));
        Assert.IsTrue(File.Exists(workerPath), $"App Worker 文件不存在：{workerPath}");
        var pythonResolver = new Diary.Script.Py.PythonRuntimeResolver();
        var python = await pythonResolver.ResolveAsync();
        if (!python.Succeeded)
        {
            Assert.Inconclusive("当前环境没有可用的 Python 3.10+ runtime。");
            return;
        }

        var catalog = new ScriptCatalog();
        RegisterSource(catalog, "lua-app", "lua", "demo.lua", "function application_main(context) return nil end");
        RegisterSource(catalog, "python-app", "python", "demo.py", "def application_main(context):\n    return None\n");
        var runtimes = new Dictionary<string, WorkerRuntime>(StringComparer.OrdinalIgnoreCase)
        {
            ["lua"] = new("lua", new WorkerSupervisor(new ProcessWorkerTransportFactory(new(
                workerPath, ["--language", "lua"], Path.GetDirectoryName(workerPath)!))),
                new("lua", [ScriptApiVersion.V1], ["workItems.query"]),
                WorkerRuntimePolicy.Shared),
            ["python"] = new("python", new WorkerSupervisor(
                new Diary.Script.Py.PythonWorkerTransportFactory(pythonResolver),
                maxRequestsPerWorker: WorkerRuntimePolicy.Dedicated.MaxRequestsPerWorker),
                new("python", [ScriptApiVersion.V1], ["workItems.query"]),
                WorkerRuntimePolicy.Dedicated),
        };
        var executor = new WorkerScriptExecutor(catalog, runtimes);
        try
        {
            foreach (var scriptId in new[] { "lua-app", "python-app" })
            {
                var outcome = await executor.ExecuteAsync(scriptId,
                     new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual));
                Assert.AreEqual(ScriptExecutionStatus.Succeeded, outcome.Result.Status,
                    string.Join("; ", outcome.Result.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
            }
        }
        finally
        {
            foreach (var runtime in runtimes.Values)
                await runtime.Supervisor.StopAsync();
        }
    }

    private static void RegisterSource(ScriptCatalog catalog, string id, string engine, string path, string source)
    {
        catalog.Register(new TestProgram(id));
        catalog.SetSource(id, new(path, source, engine));
    }

    private sealed class TestProgram(string id) : IScriptProgramV1
    {
        public ScriptDescriptor Descriptor { get; } = new(
            id, id, ScriptApiVersion.V1, ScriptScope.Application);

        public ValueTask<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptExecutionResult.Succeeded());
    }

    [TestMethod]
    public async Task ProcessTransport_CancelledPollingScriptsReturnCancelledAcrossLanguages()
    {
        var workerPath = GetWorkerPath();
        Assert.IsTrue(File.Exists(workerPath), $"Worker 文件不存在：{workerPath}");
        var dotnetPath = GetDotnetPath();
        if (dotnetPath is null)
        {
            Assert.Inconclusive("当前环境没有可用的绝对 dotnet 路径。");
            return;
        }

        var csharpDispatcher = new ProgressDispatcher();
        var luaDispatcher = new ProgressDispatcher();
        var pythonDispatcher = new ProgressDispatcher();
        var pythonRuntime = await new Diary.Script.Py.PythonRuntimeResolver().ResolveAsync();
        if (!pythonRuntime.Succeeded || pythonRuntime.ExecutablePath is null)
        {
            Assert.Inconclusive("当前环境没有可用的 Python 3.10+ runtime。");
            return;
        }

        var cases = new[]
        {
            (
                Language: "csharp",
                Supervisor: CreateDotnetSupervisor(workerPath, "csharp", dotnetPath, csharpDispatcher),
                Dispatcher: csharpDispatcher,
                Payload: new WorkerExecutePayload(
                    "cancel-csharp",
                    "cancel.cs",
                    CSharpCancellationSource,
                    new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                    new ScriptDescriptorHint("cancel-csharp", "Cancel C#", ScriptScope.Application, EngineName: "csharp"))),
            (
                Language: "lua",
                Supervisor: CreateDotnetSupervisor(workerPath, "lua", dotnetPath, luaDispatcher),
                Dispatcher: luaDispatcher,
                Payload: new WorkerExecutePayload(
                    "cancel-lua",
                    "cancel.lua",
                    "function application_main(context)\n    context.progress.report(0.1, 'started')\n    while not context.isCancelled() do end\nend\n",
                    new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                    new ScriptDescriptorHint("cancel-lua", "Cancel Lua", ScriptScope.Application, EngineName: "lua"))),
            (
                Language: "python",
                Supervisor: new WorkerSupervisor(
                    new ProcessWorkerTransportFactory(new WorkerProcessOptions(
                        pythonRuntime.ExecutablePath,
                        Diary.Script.Py.PythonWorkerSource.CreateArguments(),
                        AppContext.BaseDirectory,
                        new Dictionary<string, string>
                        {
                            ["PYTHONIOENCODING"] = "utf-8",
                            ["PYTHONUNBUFFERED"] = "1",
                        })),
                    pythonDispatcher,
                    cancellationGracePeriod: TimeSpan.FromSeconds(2)),
                Dispatcher: pythonDispatcher,
                Payload: new WorkerExecutePayload(
                    "cancel-python",
                    "cancel.py",
                    "def application_main(context):\n    context.progress.report(0.1, 'started')\n    while not context.isCancelled():\n        pass\n    return None\n",
                    new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                    new ScriptDescriptorHint("cancel-python", "Cancel Python", ScriptScope.Application, EngineName: "python"))),
        };

        foreach (var testCase in cases)
        {
            try
            {
                await testCase.Supervisor.StartAsync(new(testCase.Language, [ScriptApiVersion.V1], ["script.progress"]));
                using var cancellation = new CancellationTokenSource();
                var execution = testCase.Supervisor.ExecuteAsync(
                    testCase.Payload.ScriptId,
                    $"{testCase.Language}-cancel",
                    testCase.Payload,
                    cancellationToken: cancellation.Token).AsTask();
                var progressWait = testCase.Dispatcher.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var completed = await Task.WhenAny(execution, progressWait);
                if (completed == execution)
                {
                    var earlyResult = await execution;
                    Assert.Fail($"{testCase.Language} 在取消前结束：{earlyResult.Payload.Status}；{string.Join("; ", earlyResult.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}"))}");
                }
                await progressWait;
                cancellation.Cancel();

                var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.AreEqual(
                    ScriptExecutionStatus.Cancelled,
                    result.Payload.Status,
                    $"{testCase.Language}: {string.Join("; ", result.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}"))}");
                Assert.AreEqual(WorkerState.Ready, testCase.Supervisor.State, testCase.Language);
            }
            finally
            {
                await testCase.Supervisor.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [TestMethod]
    public async Task ProcessTransport_ReportsConsistentFailureAndTimeoutStatusesAcrossLanguages()
    {
        var workerPath = GetWorkerPath();
        Assert.IsTrue(File.Exists(workerPath), $"Worker 文件不存在：{workerPath}");
        var dotnetPath = GetDotnetPath();
        if (dotnetPath is null)
        {
            Assert.Inconclusive("当前环境没有可用的绝对 dotnet 路径。");
            return;
        }

        var pythonRuntime = await new Diary.Script.Py.PythonRuntimeResolver().ResolveAsync();
        if (!pythonRuntime.Succeeded || pythonRuntime.ExecutablePath is null)
        {
            Assert.Inconclusive("当前环境没有可用的 Python 3.10+ runtime。");
            return;
        }

        var cases = new[]
        {
            new WorkerComparisonCase(
                "csharp",
                "error-csharp",
                "error-csharp.cs",
                "public sealed class ErrorDemo : Diary.ScriptBase.IScriptProgramV1 { public Diary.ScriptBase.ScriptDescriptor Descriptor { get; } = new(\"error-csharp\", \"Error C#\", Diary.ScriptBase.ScriptApiVersion.V1, Diary.ScriptBase.ScriptScope.Application); public System.Threading.Tasks.ValueTask<Diary.ScriptBase.ScriptExecutionResult> ExecuteAsync(Diary.ScriptBase.ScriptExecutionRequest request, Diary.ScriptBase.IScriptExecutionContext context, System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.ValueTask.FromException<Diary.ScriptBase.ScriptExecutionResult>(new System.InvalidOperationException(\"expected failure\")); }",
                "SCRIPT_EXECUTION_EXCEPTION",
                false),
            new WorkerComparisonCase(
                "lua",
                "error-lua",
                "error-lua.lua",
                "function application_main(context) error('expected failure') end",
                "LUA_EXECUTION_FAILED",
                false),
            new WorkerComparisonCase(
                "python",
                "error-python",
                "error-python.py",
                "def application_main(context):\n    raise RuntimeError('expected failure')\n",
                "PYTHON_EXECUTION_FAILED",
                false),
            new WorkerComparisonCase(
                "csharp",
                "timeout-csharp",
                "timeout-csharp.cs",
                "public sealed class TimeoutDemo : Diary.ScriptBase.IScriptProgramV1 { public Diary.ScriptBase.ScriptDescriptor Descriptor { get; } = new(\"timeout-csharp\", \"Timeout C#\", Diary.ScriptBase.ScriptApiVersion.V1, Diary.ScriptBase.ScriptScope.Application); public async System.Threading.Tasks.ValueTask<Diary.ScriptBase.ScriptExecutionResult> ExecuteAsync(Diary.ScriptBase.ScriptExecutionRequest request, Diary.ScriptBase.IScriptExecutionContext context, System.Threading.CancellationToken cancellationToken = default) { while (!context.IsCancellationRequested) { } await System.Threading.Tasks.Task.Yield(); return Diary.ScriptBase.ScriptExecutionResult.Succeeded(); } }",
                "SCRIPT_EXECUTION_TIMED_OUT",
                true),
            new WorkerComparisonCase(
                "lua",
                "timeout-lua",
                "timeout-lua.lua",
                "function application_main(context) while not context.isCancelled() do end end",
                "SCRIPT_EXECUTION_TIMED_OUT",
                true),
            new WorkerComparisonCase(
                "python",
                "timeout-python",
                "timeout-python.py",
                "def application_main(context):\n    while not context.isCancelled():\n        pass\n",
                "SCRIPT_EXECUTION_TIMED_OUT",
                true),
        };

        foreach (var testCase in cases)
        {
            var supervisor = CreateSupervisor(testCase.Language, workerPath, dotnetPath, pythonRuntime.ExecutablePath);
            try
            {
                await supervisor.StartAsync(new(testCase.Language, [ScriptApiVersion.V1], []));
                var result = await supervisor.ExecuteAsync(
                    testCase.ScriptId,
                    $"{testCase.ScriptId}-execution",
                    new WorkerExecutePayload(
                        testCase.ScriptId,
                        testCase.SourcePath,
                        testCase.Source,
                        new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                        new ScriptDescriptorHint(testCase.ScriptId, testCase.ScriptId, ScriptScope.Application, EngineName: testCase.Language)),
                    timeout: testCase.IsTimeout ? TimeSpan.FromMilliseconds(250) : null);

                var detail = string.Join("; ", result.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}"));
                var expectedStatus = testCase.IsTimeout ? ScriptExecutionStatus.TimedOut : ScriptExecutionStatus.Failed;
                Assert.AreEqual(expectedStatus, result.Payload.Status, $"{testCase.Language}: {detail}");
                Assert.AreEqual(testCase.ExpectedDiagnosticCode, result.Payload.Diagnostics.Single().Code, $"{testCase.Language}: {detail}");
                if (testCase.IsTimeout)
                    Assert.AreEqual(WorkerState.Failed, supervisor.State, testCase.Language);
            }
            finally
            {
                await supervisor.StopAsync();
            }
        }
    }

    private static WorkerSupervisor CreateSupervisor(
        string language,
        string workerPath,
        string dotnetPath,
        string pythonPath) =>
        language switch
        {
            "python" => new WorkerSupervisor(
                new ProcessWorkerTransportFactory(new WorkerProcessOptions(
                    pythonPath,
                    Diary.Script.Py.PythonWorkerSource.CreateArguments(),
                    AppContext.BaseDirectory,
                    new Dictionary<string, string>
                    {
                        ["PYTHONIOENCODING"] = "utf-8",
                        ["PYTHONUNBUFFERED"] = "1",
                    }))),
            _ => CreateDotnetSupervisor(workerPath, language, dotnetPath),
        };

    private sealed record WorkerComparisonCase(
        string Language,
        string ScriptId,
        string SourcePath,
        string Source,
        string ExpectedDiagnosticCode,
        bool IsTimeout);

    [TestMethod]
    public async Task ProcessTransport_ReportsExitCodeWhenProcessTerminates()
    {
        using var process = StartTestProcess("exit 7", "exit /b 7");
        await process.WaitForExitAsync();
        await using var transport = new ProcessWorkerTransport(process);

        Assert.AreEqual(7, ((IWorkerTerminationNotification)transport).ExitCode);
    }

    [TestMethod]
    public async Task ProcessTransport_StopKillsProcessAfterGracePeriod()
    {
        using var process = StartTestProcess("trap '' TERM; sleep 30", "timeout /t 30 /nobreak > nul");
        await using var transport = new ProcessWorkerTransport(
            process,
            shutdownGracePeriod: TimeSpan.FromMilliseconds(50));

        await transport.StopAsync();

        Assert.IsTrue(process.HasExited);
    }

    [TestMethod]
    public async Task ProcessTransport_StopKillsProcessWhenCallerCancellationIsRequested()
    {
        using var process = StartTestProcess("trap '' TERM; sleep 30", "timeout /t 30 /nobreak > nul");
        await using var transport = new ProcessWorkerTransport(
            process,
            shutdownGracePeriod: TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await transport.StopAsync(cancellation.Token);

        Assert.IsTrue(process.HasExited);
    }

    private static System.Diagnostics.Process StartTestProcess(string unixCommand, string windowsCommand)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : "/bin/sh",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? windowsCommand : unixCommand);
        var process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
        Assert.IsTrue(process.Start());
        return process;
    }

    [TestMethod]
    public async Task Factory_RejectsRelativeExecutableAndWorkingDirectory()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new ProcessWorkerTransportFactory(new("worker", [], "/tmp")).CreateAsync().AsTask());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            new ProcessWorkerTransportFactory(new("/bin/sh", [], "tmp")).CreateAsync().AsTask());
    }

    private static WorkerSupervisor CreateDotnetSupervisor(
        string workerPath,
        string language,
        string? dotnetPath = null,
        IWorkerHostCallDispatcher? dispatcher = null) =>
        new(new ProcessWorkerTransportFactory(new(
            dotnetPath ?? GetDotnetPath() ?? throw new InvalidOperationException("dotnet 路径不可用。"),
            [workerPath, "--language", language],
            Path.GetDirectoryName(workerPath)!,
            new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" })),
            dispatcher,
            cancellationGracePeriod: TimeSpan.FromSeconds(2));
    private static string? GetDotnetPath()
    {
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
            "/usr/share/dotnet",
        };
        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.Combine(root!, executableName))
            .FirstOrDefault(File.Exists);
    }

    private static string GetWorkerPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "../../../../Diary.Script.Worker/bin/Debug/net10.0/Diary.Script.Worker.dll"));

    private const string CSharpCancellationSource = """
public sealed class CancelDemo : Diary.ScriptBase.IScriptProgramV1
{
    public Diary.ScriptBase.ScriptDescriptor Descriptor { get; } = new("cancel-csharp", "Cancel C#", Diary.ScriptBase.ScriptApiVersion.V1, Diary.ScriptBase.ScriptScope.Application);

    public async System.Threading.Tasks.ValueTask<Diary.ScriptBase.ScriptExecutionResult> ExecuteAsync(
        Diary.ScriptBase.ScriptExecutionRequest request,
        Diary.ScriptBase.IScriptExecutionContext context,
        System.Threading.CancellationToken cancellationToken = default)
    {
        await context.ReportProgressAsync(new Diary.ScriptBase.ScriptProgressUpdate(0.1, "started"));
        while (!context.IsCancellationRequested) { }
        return Diary.ScriptBase.ScriptExecutionResult.Succeeded();
    }
}
""";

    private sealed class ProgressDispatcher : IWorkerHostCallDispatcher
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<WorkerHostResultPayload> DispatchAsync(
            string executionId,
            WorkerHostCallPayload call,
            CancellationToken cancellationToken = default)
        {
            if (call.Method == "script.progress")
                Started.TrySetResult(true);
            return ValueTask.FromResult(new WorkerHostResultPayload(true));
        }
    }
}
