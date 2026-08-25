using System.Collections.Immutable;
using System.Text.Json;
using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ProcessWorkerTransportTests
{
    [TestMethod]
    public async Task ProcessTransport_CompletesHandshakeHealthCheckAndExecution()
    {
        var workerPath = GetWorkerPath();
        Assert.IsTrue(File.Exists(workerPath), $"Worker 文件不存在：{workerPath}");
        var factory = new ProcessWorkerTransportFactory(new(
            GetRequiredDotnetPath(),
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
        var workerPath = GetWorkerPath();
        var factory = new ProcessWorkerTransportFactory(new(
            GetRequiredDotnetPath(),
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
        var runtime = await GetRequiredPythonRuntimeAsync();

        var supervisor = new WorkerSupervisor(
            new ProcessWorkerTransportFactory(new WorkerProcessOptions(
                runtime.ExecutablePath!,
                Diary.Script.Py.PythonWorkerSource.CreateArguments(),
                AppContext.BaseDirectory,
                new Dictionary<string, string>
                {
                    ["PYTHONIOENCODING"] = "utf-8",
                    ["PYTHONUNBUFFERED"] = "1",
                })),
            handshakeTimeout: TimeSpan.FromSeconds(30));
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
        var workerPath = GetAppWorkerPath();
        Assert.IsTrue(File.Exists(workerPath), $"App Worker 文件不存在：{workerPath}");
        var pythonResolver = new Diary.Script.Py.PythonRuntimeResolver();
        _ = await GetRequiredPythonRuntimeAsync(pythonResolver);

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

    [TestMethod]
    public async Task WorkerScriptExecutor_ExecutesMixedV1V2AndNormalizedArgumentsAcrossLanguages()
    {
        var workerPath = GetWorkerPath();
        Assert.IsTrue(File.Exists(workerPath), $"Worker 文件不存在：{workerPath}");
        var pythonRuntime = await GetRequiredPythonRuntimeAsync();
        var parameters = new[]
        {
            new ScriptParameterDefinition("count", "Count", ScriptParameterType.Integer, Required: true),
            new ScriptParameterDefinition("enabled", "Enabled", ScriptParameterType.Boolean, Required: true),
        };
        var catalog = new ScriptCatalog();
        RegisterSource(
            catalog,
            "mixed-v1",
            "csharp",
            "mixed-v1.cs",
            "public sealed class MixedV1 : Diary.ScriptBase.ApplicationScript { public override string Id => \"mixed-v1\"; public override string Name => \"Mixed V1\"; public override System.Threading.Tasks.ValueTask<Diary.ScriptBase.ScriptExecutionResult> ExecuteAsync(Diary.ScriptBase.IScriptApplicationContext context, System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.ValueTask.FromResult(Diary.ScriptBase.ScriptExecutionResult.Succeeded()); }");
        RegisterSource(
            catalog,
            "mixed-v2-csharp",
            "csharp",
            "mixed-v2.cs",
            """
                public sealed class MixedV2 : Diary.ScriptBase.ApplicationScriptV2
                {
                    public override string Id => "mixed-v2-csharp";
                    public override string Name => "Mixed V2 C#";
                    public override System.Collections.Generic.IReadOnlyList<Diary.ScriptBase.ScriptParameterDefinition> Parameters =>
                    [
                        new("count", "Count", Diary.ScriptBase.ScriptParameterType.Integer, Required: true),
                        new("enabled", "Enabled", Diary.ScriptBase.ScriptParameterType.Boolean, Required: true),
                    ];
                    public override System.Threading.Tasks.ValueTask<Diary.ScriptBase.ScriptExecutionResult> ExecuteAsync(
                        Diary.ScriptBase.IScriptApplicationContext context,
                        System.Threading.CancellationToken cancellationToken = default)
                    {
                        if (context.Arguments["count"] != "42" || context.Arguments["enabled"] != "true")
                            throw new System.InvalidOperationException("C# V2 arguments were not normalized.");
                        return System.Threading.Tasks.ValueTask.FromResult(Diary.ScriptBase.ScriptExecutionResult.Succeeded());
                    }
                }
                """,
            new ScriptDescriptor(
                "mixed-v2-csharp",
                "Mixed V2 C#",
                ScriptApiVersion.V2,
                ScriptScope.Application,
                Parameters: parameters));
        RegisterSource(
            catalog,
            "mixed-v2-lua",
            "lua",
            "mixed-v2.lua",
            """
                function application_main(context)
                  if context.arguments.count ~= "42" or context.arguments.enabled ~= "true" then
                    error("Lua V2 arguments were not normalized")
                  end
                  return nil
                end
                """,
            new ScriptDescriptor(
                "mixed-v2-lua",
                "Mixed V2 Lua",
                ScriptApiVersion.V2,
                ScriptScope.Application,
                Parameters: parameters));
        var pythonDescriptor = new ScriptDescriptor(
            "mixed-v2-python",
            "Mixed V2 Python",
            ScriptApiVersion.V2,
            ScriptScope.Application,
            EntryKind: ScriptEntryKind.Automation,
            Parameters: [new("project", "Project", ScriptParameterType.String, Required: true)]);
        RegisterSource(
            catalog,
            pythonDescriptor.Id,
            "python",
            "mixed-v2.py",
            """
                def automation_main(context):
                    if context.arguments.get("project") != "diary":
                        raise RuntimeError("Python V2 defaults were not bound")
                    if "workItemId" in context.arguments:
                        raise RuntimeError("Automation event data leaked into V2 arguments")
                    if context.automation["eventData"].get("workItemId") != "42":
                        raise RuntimeError("Automation event data was not exposed")
                    return None
                """,
            pythonDescriptor,
            new Dictionary<string, string> { ["project"] = "diary" });

        var runtimes = new Dictionary<string, WorkerRuntime>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp"] = new(
                "csharp",
                CreateDotnetSupervisor(workerPath, "csharp"),
                new("csharp", [ScriptApiVersion.V1, ScriptApiVersion.V2], []),
                WorkerRuntimePolicy.Shared),
            ["lua"] = new(
                "lua",
                CreateDotnetSupervisor(workerPath, "lua"),
                new("lua", [ScriptApiVersion.V1, ScriptApiVersion.V2], []),
                WorkerRuntimePolicy.Shared),
            ["python"] = new(
                "python",
                new WorkerSupervisor(
                    new ProcessWorkerTransportFactory(new WorkerProcessOptions(
                        pythonRuntime.ExecutablePath!,
                        Diary.Script.Py.PythonWorkerSource.CreateArguments(),
                        AppContext.BaseDirectory,
                        new Dictionary<string, string>
                        {
                            ["PYTHONIOENCODING"] = "utf-8",
                            ["PYTHONUNBUFFERED"] = "1",
                        })),
                    handshakeTimeout: TimeSpan.FromSeconds(30)),
                new("python", [ScriptApiVersion.V1, ScriptApiVersion.V2], []),
                WorkerRuntimePolicy.Dedicated),
        };
        var workerExecutor = new WorkerScriptExecutor(catalog, runtimes);
        var manager = new ScriptManager(
            new ScriptBuildService(new ScriptEngineRegistry()),
            catalog,
            new ScriptExecutor(),
            workerExecutor: workerExecutor);
        var suppliedArguments = ImmutableDictionary<string, string>.Empty
            .Add("count", "+0042")
            .Add("enabled", "True");
        try
        {
            var v1 = await manager.ExecuteAsync("mixed-v1", new ScriptExecutionRequest());
            Assert.AreEqual(ScriptExecutionStatus.Succeeded, v1.Result.Status, JoinDiagnostics(v1.Result.Diagnostics));

            foreach (var scriptId in new[] { "mixed-v2-csharp", "mixed-v2-lua" })
            {
                var outcome = await manager.ExecuteAsync(
                    scriptId,
                    new ScriptExecutionRequest(Arguments: suppliedArguments));
                Assert.AreEqual(
                    ScriptExecutionStatus.Succeeded,
                    outcome.Result.Status,
                    $"{scriptId}: {JoinDiagnostics(outcome.Result.Diagnostics)}");
            }

            var python = await manager.ExecuteAsync(
                pythonDescriptor.Id,
                new ScriptExecutionRequest(
                    Source: ScriptExecutionSource.WorkItemCreated,
                    AutomationEventData: ImmutableDictionary<string, string>.Empty.Add("workItemId", "42")));
            Assert.AreEqual(
                ScriptExecutionStatus.Succeeded,
                python.Result.Status,
                JoinDiagnostics(python.Result.Diagnostics));
        }
        finally
        {
            await workerExecutor.StopAllAsync();
        }
    }

    private static void RegisterSource(
        ScriptCatalog catalog,
        string id,
        string engine,
        string path,
        string source,
        ScriptDescriptor? descriptor = null,
        IReadOnlyDictionary<string, string>? defaultArguments = null)
    {
        catalog.Register(new TestProgram(descriptor ?? new ScriptDescriptor(
            id,
            id,
            ScriptApiVersion.V1,
            ScriptScope.Application)));
        catalog.SetSource(id, new(path, source, engine, defaultArguments));
    }

    private sealed class TestProgram(ScriptDescriptor descriptor) : IScriptProgramV1
    {
        public ScriptDescriptor Descriptor { get; } = descriptor;

        public ValueTask<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptExecutionResult.Succeeded());
    }

    private static string JoinDiagnostics(IEnumerable<ScriptDiagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(item => $"{item.Code}: {item.Message}"));

    [TestMethod]
    public async Task ProcessTransport_CancelledPollingScriptsRemainRecoverableAcrossLanguages()
    {
        var workerPath = GetWorkerPath();
        Assert.IsTrue(File.Exists(workerPath), $"Worker 文件不存在：{workerPath}");
        var dotnetPath = GetRequiredDotnetPath();

        var csharpDispatcher = new ProgressDispatcher();
        var luaDispatcher = new ProgressDispatcher();
        var pythonDispatcher = new ProgressDispatcher();
        var pythonRuntime = await GetRequiredPythonRuntimeAsync();

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
                        pythonRuntime.ExecutablePath!,
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
            var handshake = new WorkerHandshakeOptions(
                testCase.Language,
                [ScriptApiVersion.V1],
                ["script.progress"]);
            try
            {
                await testCase.Supervisor.StartAsync(handshake);
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
                if (testCase.Supervisor.State == WorkerState.Failed)
                {
                    Assert.AreEqual(
                        "WORKER_CANCEL_GRACE_EXPIRED",
                        result.Payload.Diagnostics.Single().Code,
                        testCase.Language);
                    await testCase.Supervisor.StartAsync(handshake);
                }
                else
                {
                    Assert.AreEqual(WorkerState.Ready, testCase.Supervisor.State, testCase.Language);
                    Assert.IsFalse(result.Payload.Diagnostics.Any(item =>
                        item.Code == "WORKER_CANCEL_GRACE_EXPIRED"), testCase.Language);
                }

                Assert.AreEqual(WorkerState.Ready, testCase.Supervisor.State, $"{testCase.Language} 取消后不可恢复");
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
        var dotnetPath = GetRequiredDotnetPath();

        var pythonRuntime = await GetRequiredPythonRuntimeAsync();

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
            var supervisor = CreateSupervisor(testCase.Language, workerPath, dotnetPath, pythonRuntime.ExecutablePath!);
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
    public async Task ProcessTransport_TerminationDiagnosticsWaitForExitCodeAndStderr()
    {
        using var process = StartTestProcess(
            "echo 'transport failed' >&2; exit 7",
            "echo transport failed 1>&2 & exit /b 7");
        await using var transport = new ProcessWorkerTransport(process);
        var notification = (IWorkerTerminationNotification)transport;

        await notification.WaitForTerminationDiagnosticsAsync();

        Assert.AreEqual(7, notification.ExitCode);
        StringAssert.Contains(notification.StderrSummary, "transport failed");
    }

    [TestMethod]
    public async Task Supervisor_ReportsExitCodeAndStderrWhenWorkerExitsBeforeHandshake()
    {
        var supervisor = CreateEarlyExitSupervisor();

        var exception = await Assert.ThrowsExactlyAsync<WorkerProtocolException>(() =>
            supervisor.StartAsync(new("csharp", [ScriptApiVersion.V1], [])).AsTask());

        Assert.AreEqual("WORKER_HANDSHAKE_PROCESS_EXITED", exception.Diagnostic.Code);
        StringAssert.Contains(exception.Diagnostic.Message, "退出码：7");
        StringAssert.Contains(exception.Diagnostic.Message, "CET startup failed");
        Assert.AreEqual(WorkerState.Failed, supervisor.State);
    }

    [TestMethod]
    public async Task WorkerScriptExecutor_PreservesHandshakeExitDiagnostic()
    {
        var catalog = new ScriptCatalog();
        RegisterSource(catalog, "csharp-fail", "csharp", "fail.cs", "// Worker exits before compiling this source.");
        var supervisor = CreateEarlyExitSupervisor();
        var executor = new WorkerScriptExecutor(catalog, new Dictionary<string, WorkerRuntime>
        {
            ["csharp"] = new("csharp", supervisor,
                new("csharp", [ScriptApiVersion.V1], []), WorkerRuntimePolicy.Shared),
        });

        var outcome = await executor.ExecuteAsync(
            "csharp-fail", new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual));

        Assert.AreEqual(ScriptExecutionStatus.Failed, outcome.Result.Status);
        Assert.AreEqual("WORKER_HANDSHAKE_PROCESS_EXITED", outcome.Result.Diagnostics.Single().Code);
        StringAssert.Contains(outcome.Result.Diagnostics.Single().Message, "CET startup failed");
        await executor.StopAllAsync();
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

    private static WorkerSupervisor CreateEarlyExitSupervisor()
    {
        var executable = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "C:\\Windows\\System32\\cmd.exe"
            : "/bin/sh";
        var arguments = OperatingSystem.IsWindows()
            ? new[] { "/c", "echo CET startup failed 1>&2 & exit /b 7" }
            : new[] { "-c", "echo 'CET startup failed' >&2; exit 7" };
        return new WorkerSupervisor(new ProcessWorkerTransportFactory(new(
            Path.GetFullPath(executable), arguments, AppContext.BaseDirectory)));
    }

    [TestMethod]
    public async Task ProcessTransport_ForwardsScriptPrintToLogAcrossLanguages()
    {
        var workerPath = GetWorkerPath();
        Assert.IsTrue(File.Exists(workerPath), $"Worker 文件不存在：{workerPath}");
        var dotnetPath = GetRequiredDotnetPath();

        var pythonRuntime = await GetRequiredPythonRuntimeAsync();

        var cases = new[]
        {
            new PrintCase(
                "csharp",
                dispatcher => CreateDotnetSupervisor(workerPath, "csharp", dotnetPath, dispatcher),
                new WorkerExecutePayload(
                    "print-csharp",
                    "print.cs",
                    "public sealed class PrintDemo : Diary.ScriptBase.IScriptProgramV1 { public Diary.ScriptBase.ScriptDescriptor Descriptor { get; } = new(\"print-csharp\", \"Print C#\", Diary.ScriptBase.ScriptApiVersion.V1, Diary.ScriptBase.ScriptScope.Application); public async System.Threading.Tasks.ValueTask<Diary.ScriptBase.ScriptExecutionResult> ExecuteAsync(Diary.ScriptBase.ScriptExecutionRequest request, Diary.ScriptBase.IScriptExecutionContext context, System.Threading.CancellationToken cancellationToken = default) { System.Console.WriteLine(\"第一行\"); System.Console.Write(\"第二行\\n\"); System.Console.Write(\"末尾无换行\"); return Diary.ScriptBase.ScriptExecutionResult.Succeeded(); } }",
                    new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                    new ScriptDescriptorHint("print-csharp", "Print C#", ScriptScope.Application, EngineName: "csharp")),
                ["第一行", "第二行", "末尾无换行"]),
            new PrintCase(
                "lua",
                dispatcher => CreateDotnetSupervisor(workerPath, "lua", dotnetPath, dispatcher),
                new WorkerExecutePayload(
                    "print-lua",
                    "print.lua",
                    "function application_main(context)\n    print('lua 第一行')\n    print('a', 'b')\n    print('末尾无换行尾')\nend\n",
                    new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                    new ScriptDescriptorHint("print-lua", "Print Lua", ScriptScope.Application, EngineName: "lua")),
                ["lua 第一行", "a\tb", "末尾无换行尾"]),
            new PrintCase(
                "python",
                dispatcher => new WorkerSupervisor(
                    new ProcessWorkerTransportFactory(new WorkerProcessOptions(
                        pythonRuntime.ExecutablePath!,
                        Diary.Script.Py.PythonWorkerSource.CreateArguments(),
                        AppContext.BaseDirectory,
                        new Dictionary<string, string>
                        {
                            ["PYTHONIOENCODING"] = "utf-8",
                            ["PYTHONUNBUFFERED"] = "1",
                        })),
                    dispatcher,
                    cancellationGracePeriod: TimeSpan.FromSeconds(2)),
                new WorkerExecutePayload(
                    "print-python",
                    "print.py",
                    "def application_main(context):\n    print('python 第一行')\n    print('a', 'b')\n    print('末尾无换行尾', end='')\n    return None\n",
                    new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                    new ScriptDescriptorHint("print-python", "Print Python", ScriptScope.Application, EngineName: "python")),
                ["python 第一行", "a b", "末尾无换行尾"]),
        };

        foreach (var testCase in cases)
        {
            var dispatcher = new LogRecordingDispatcher();
            var supervisor = testCase.SupervisorFactory(dispatcher);
            try
            {
                await supervisor.StartAsync(new(testCase.Language, [ScriptApiVersion.V1], ["log.write"]));
                var result = await supervisor.ExecuteAsync(
                    testCase.Payload.ScriptId,
                    $"{testCase.Language}-print",
                    testCase.Payload,
                    TimeSpan.FromSeconds(30));

                Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status,
                    $"{testCase.Language}: {string.Join("; ", result.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}"))}");
                Assert.AreEqual(
                    string.Join("|", testCase.ExpectedMessages),
                    string.Join("|", dispatcher.InfoMessages),
                    $"{testCase.Language} 打印消息不匹配：期望 [{string.Join("|", testCase.ExpectedMessages)}] 实际 [{string.Join("|", dispatcher.InfoMessages)}]");
            }
            finally
            {
                await supervisor.StopAsync();
            }
        }
    }

    private sealed record PrintCase(
        string Language,
        Func<IWorkerHostCallDispatcher, WorkerSupervisor> SupervisorFactory,
        WorkerExecutePayload Payload,
        string[] ExpectedMessages);

    private sealed class LogRecordingDispatcher : IWorkerHostCallDispatcher
    {
        public List<string> InfoMessages { get; } = [];

        public ValueTask<WorkerHostResultPayload> DispatchAsync(
            string executionId,
            WorkerHostCallPayload call,
            CancellationToken cancellationToken = default)
        {
            if (call.Method == "log.write"
                && call.Params.ValueKind == JsonValueKind.Object
                && call.Params.TryGetProperty("level", out var level)
                && level.GetString() == "Info"
                && call.Params.TryGetProperty("message", out var message))
                InfoMessages.Add(message.GetString() ?? string.Empty);
            return ValueTask.FromResult(new WorkerHostResultPayload(true));
        }
    }

    [TestMethod]
    public async Task ProcessTransport_RequestsMainWindowActivationAcrossLanguages()
    {
        var workerPath = GetWorkerPath();
        Assert.IsTrue(File.Exists(workerPath), $"Worker 文件不存在：{workerPath}");
        var dotnetPath = GetRequiredDotnetPath();
        var pythonRuntime = await GetRequiredPythonRuntimeAsync();

        var cases = new[]
        {
            new WindowActivationCase(
                "csharp",
                dispatcher => CreateDotnetSupervisor(workerPath, "csharp", dotnetPath, dispatcher),
                new WorkerExecutePayload(
                    "window-csharp",
                    "window.cs",
                    """
                    using Diary.ScriptBase;
                    using Diary.ScriptHost;

                    public sealed class WindowDemo : IScriptProgramV1
                    {
                        public ScriptDescriptor Descriptor { get; } = new(
                            "window-csharp", "Window C#", ScriptApiVersion.V1, ScriptScope.Application);

                        public async System.Threading.Tasks.ValueTask<ScriptExecutionResult> ExecuteAsync(
                            ScriptExecutionRequest request,
                            IScriptExecutionContext context,
                            System.Threading.CancellationToken cancellationToken = default)
                        {
                            _ = context.GetRequiredApi<SysApi>();
                            await context.GetRequiredApi<ISysApi>()
                                .RequestMainWindowActivationAsync(cancellationToken);
                            return ScriptExecutionResult.Succeeded();
                        }
                    }
                    """,
                    new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                    new ScriptDescriptorHint("window-csharp", "Window C#", ScriptScope.Application, EngineName: "csharp"))),
            new WindowActivationCase(
                "lua",
                dispatcher => CreateDotnetSupervisor(workerPath, "lua", dotnetPath, dispatcher),
                new WorkerExecutePayload(
                    "window-lua",
                    "window.lua",
                    "function application_main(context)\n    diary.ui.request_main_window_activation()\nend\n",
                    new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                    new ScriptDescriptorHint("window-lua", "Window Lua", ScriptScope.Application, EngineName: "lua"))),
            new WindowActivationCase(
                "python",
                dispatcher => new WorkerSupervisor(
                    new ProcessWorkerTransportFactory(new WorkerProcessOptions(
                        pythonRuntime.ExecutablePath!,
                        Diary.Script.Py.PythonWorkerSource.CreateArguments(),
                        AppContext.BaseDirectory,
                        new Dictionary<string, string>
                        {
                            ["PYTHONIOENCODING"] = "utf-8",
                            ["PYTHONUNBUFFERED"] = "1",
                        })),
                    dispatcher,
                    cancellationGracePeriod: TimeSpan.FromSeconds(2)),
                new WorkerExecutePayload(
                    "window-python",
                    "window.py",
                    "def application_main(context):\n    context.diary.ui.request_main_window_activation()\n    return None\n",
                    new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                    new ScriptDescriptorHint("window-python", "Window Python", ScriptScope.Application, EngineName: "python"))),
        };

        foreach (var testCase in cases)
        {
            var dispatcher = new MethodRecordingDispatcher();
            var supervisor = testCase.SupervisorFactory(dispatcher);
            try
            {
                await supervisor.StartAsync(new(
                    testCase.Language,
                    [ScriptApiVersion.V1],
                    ["ui.window.raise"]));
                var result = await supervisor.ExecuteAsync(
                    testCase.Payload.ScriptId,
                    $"{testCase.Language}-window",
                    testCase.Payload,
                    TimeSpan.FromSeconds(30));

                Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status,
                    $"{testCase.Language}: {string.Join("; ", result.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}"))}");
                CollectionAssert.AreEqual(
                    new[] { "ui.window.raise" },
                    dispatcher.Methods,
                    $"{testCase.Language} 的窗口激活 HostCall 不匹配。");
            }
            finally
            {
                await supervisor.StopAsync();
            }
        }
    }

    private sealed record WindowActivationCase(
        string Language,
        Func<IWorkerHostCallDispatcher, WorkerSupervisor> SupervisorFactory,
        WorkerExecutePayload Payload);

    private sealed class MethodRecordingDispatcher : IWorkerHostCallDispatcher
    {
        public List<string> Methods { get; } = [];

        public ValueTask<WorkerHostResultPayload> DispatchAsync(
            string executionId,
            WorkerHostCallPayload call,
            CancellationToken cancellationToken = default)
        {
            Methods.Add(call.Method);
            return ValueTask.FromResult(new WorkerHostResultPayload(true));
        }
    }

    [TestMethod]
    public async Task ProcessTransport_PassesEffectsThroughLuaAndPython()
    {
        var workerPath = GetWorkerPath();
        Assert.IsTrue(File.Exists(workerPath), $"Worker 文件不存在：{workerPath}");
        var dotnetPath = GetRequiredDotnetPath();

        var pythonRuntime = await GetRequiredPythonRuntimeAsync();

        var cases = new[]
        {
            (
                Language: "lua",
                Supervisor: CreateDotnetSupervisor(workerPath, "lua", dotnetPath),
                Payload: new WorkerExecutePayload(
                    "effects-lua",
                    "effects.lua",
                    "function application_main(context)\n    return { succeeded = true, effects = { appendedCount = 1, idempotencyKey = 'lua-key', createdWorkItemIds = { 42 } } }\nend\n",
                    new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                    new ScriptDescriptorHint("effects-lua", "Effects Lua", ScriptScope.Application, EngineName: "lua"))),
            (
                Language: "python",
                Supervisor: new WorkerSupervisor(
                    new ProcessWorkerTransportFactory(new WorkerProcessOptions(
                        pythonRuntime.ExecutablePath!,
                        Diary.Script.Py.PythonWorkerSource.CreateArguments(),
                        AppContext.BaseDirectory,
                        new Dictionary<string, string>
                        {
                            ["PYTHONIOENCODING"] = "utf-8",
                            ["PYTHONUNBUFFERED"] = "1",
                        }))),
                Payload: new WorkerExecutePayload(
                    "effects-python",
                    "effects.py",
                    "def application_main(context):\n    return {\"succeeded\": True, \"effects\": {\"appendedCount\": 2, \"idempotencyKey\": \"py-key\", \"createdWorkItemIds\": [7, 8]}}\n",
                    new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
                    new ScriptDescriptorHint("effects-python", "Effects Python", ScriptScope.Application, EngineName: "python"))),
        };

        foreach (var testCase in cases)
        {
            try
            {
                await testCase.Supervisor.StartAsync(new(testCase.Language, [ScriptApiVersion.V1], []));
                var result = await testCase.Supervisor.ExecuteAsync(
                    testCase.Payload.ScriptId,
                    $"{testCase.Language}-effects",
                    testCase.Payload);

                Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status,
                    $"{testCase.Language}: {string.Join("; ", result.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}"))}");
                Assert.IsNotNull(result.Payload.Effects, testCase.Language);
                Assert.AreEqual(testCase.Language == "lua" ? 1 : 2, result.Payload.Effects!.AppendedCount, testCase.Language);
                Assert.AreEqual(testCase.Language == "lua" ? "lua-key" : "py-key", result.Payload.Effects.IdempotencyKey, testCase.Language);
                CollectionAssert.AreEqual(
                    testCase.Language == "lua" ? new[] { 42 } : new[] { 7, 8 },
                    result.Payload.Effects.CreatedWorkItemIds!.ToArray(),
                    testCase.Language);
            }
            finally
            {
                await testCase.Supervisor.StopAsync();
            }
        }
    }

    [TestMethod]
    public async Task ProcessTransport_WorkerHonorsNegotiatedLimits()
    {
        var workerPath = GetWorkerPath();
        var supervisor = new WorkerSupervisor(
            new ProcessWorkerTransportFactory(new(
                GetRequiredDotnetPath(),
                [workerPath, "--language", "csharp"],
                Path.GetDirectoryName(workerPath)!,
                new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" })),
            maxResultMessageBytes: 128 * 1024,
            cancellationGracePeriod: TimeSpan.FromSeconds(2));
        try
        {
            // 协商较小的消息/结果上限，正常脚本仍能完整往返
            await supervisor.StartAsync(new(
                "csharp",
                [ScriptApiVersion.V1],
                [],
                MaxMessageBytes: 64 * 1024,
                MaxResultMessageBytes: 128 * 1024));
            var result = await supervisor.ExecuteAsync("demo", "exec-1", new
            {
                ScriptId = "demo",
                SourcePath = "demo.cs",
                Source = "public sealed class Demo : Diary.ScriptBase.IScriptProgramV1 { public Diary.ScriptBase.ScriptDescriptor Descriptor { get; } = new(\"demo\", \"Demo\", Diary.ScriptBase.ScriptApiVersion.V1, Diary.ScriptBase.ScriptScope.Application); public System.Threading.Tasks.ValueTask<Diary.ScriptBase.ScriptExecutionResult> ExecuteAsync(Diary.ScriptBase.ScriptExecutionRequest request, Diary.ScriptBase.IScriptExecutionContext context, System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.ValueTask.FromResult(Diary.ScriptBase.ScriptExecutionResult.Succeeded()); }",
                Request = new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
            });

            Assert.AreEqual(ScriptExecutionStatus.Succeeded, result.Payload.Status,
                string.Join("; ", result.Payload.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
            Assert.AreEqual(WorkerState.Ready, supervisor.State);
        }
        finally
        {
            await supervisor.StopAsync();
        }
    }

    [TestMethod]
    public async Task ProcessTransport_WorkerReportsOversizedResultCleanly()
    {
        var workerPath = GetWorkerPath();
        var supervisor = new WorkerSupervisor(
            new ProcessWorkerTransportFactory(new(
                GetRequiredDotnetPath(),
                [workerPath, "--language", "csharp"],
                Path.GetDirectoryName(workerPath)!,
                new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" })),
            maxResultMessageBytes: 64 * 1024,
            cancellationGracePeriod: TimeSpan.FromSeconds(2));
        try
        {
            await supervisor.StartAsync(new(
                "csharp",
                [ScriptApiVersion.V1],
                [],
                MaxMessageBytes: 4 * 1024 * 1024,
                MaxResultMessageBytes: 64 * 1024));
            var oversized = new string('x', 128 * 1024);
            var result = await supervisor.ExecuteAsync("demo", "exec-big-result", new
            {
                ScriptId = "demo",
                SourcePath = "demo.cs",
                Source = $"public sealed class Demo : Diary.ScriptBase.IScriptProgramV1 {{ public Diary.ScriptBase.ScriptDescriptor Descriptor {{ get; }} = new(\"demo\", \"Demo\", Diary.ScriptBase.ScriptApiVersion.V1, Diary.ScriptBase.ScriptScope.Application); public System.Threading.Tasks.ValueTask<Diary.ScriptBase.ScriptExecutionResult> ExecuteAsync(Diary.ScriptBase.ScriptExecutionRequest request, Diary.ScriptBase.IScriptExecutionContext context, System.Threading.CancellationToken cancellationToken = default) => System.Threading.Tasks.ValueTask.FromResult(new Diary.ScriptBase.ScriptExecutionResult(Diary.ScriptBase.ScriptExecutionStatus.Succeeded, [new Diary.ScriptBase.ScriptDiagnostic(\"BIG\", \"{oversized}\", Diary.ScriptBase.ScriptDiagnosticSeverity.Info, Diary.ScriptBase.ScriptDiagnosticCategory.Runtime)])); }}",
                Request = new ScriptExecutionRequest(Source: ScriptExecutionSource.Manual),
            });

            Assert.AreEqual(ScriptExecutionStatus.Failed, result.Payload.Status);
            Assert.AreEqual("WORKER_RESULT_TOO_LARGE", result.Payload.Diagnostics.Single().Code);
        }
        finally
        {
            await supervisor.StopAsync();
        }
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
            dotnetPath ?? GetRequiredDotnetPath(),
            [workerPath, "--language", language],
            Path.GetDirectoryName(workerPath)!,
            new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en-US" })),
            dispatcher,
            cancellationGracePeriod: TimeSpan.FromSeconds(2));
    private const string RequirePythonTestsEnvironmentVariable = "DIARY_REQUIRE_PYTHON_TESTS";

    private static string GetRequiredDotnetPath() =>
        GetDotnetPath() ?? throw new AssertFailedException("当前环境没有可用的绝对 dotnet 路径。");

    private static string? GetDotnetPath()
    {
        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var processPath = Environment.ProcessPath;
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            string.Equals(Path.GetFileName(processPath), executableName, StringComparison.OrdinalIgnoreCase)
                ? processPath
                : null,
            CombineExecutable(Environment.GetEnvironmentVariable("DOTNET_ROOT"), executableName),
            CombineExecutable(Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"), executableName),
            CombineExecutable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"), executableName),
            CombineExecutable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"), executableName),
            CombineExecutable("/usr/share/dotnet", executableName),
        };
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.AddRange(path
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => CombineExecutable(directory, executableName)));
        }

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => Path.GetFullPath(candidate!))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .FirstOrDefault(File.Exists);
    }

    private static string? CombineExecutable(string? directory, string executableName) =>
        string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, executableName);

    private static async Task<Diary.Script.Py.PythonRuntimeResolution> GetRequiredPythonRuntimeAsync(
        Diary.Script.Py.PythonRuntimeResolver? resolver = null)
    {
        var runtime = await (resolver ?? new Diary.Script.Py.PythonRuntimeResolver()).ResolveAsync();
        if (runtime.Succeeded && runtime.ExecutablePath is not null)
            return runtime;

        var detail = string.Join("; ", runtime.Diagnostics.Select(diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
        var message = $"当前环境没有可用的 Python 3.10+ runtime。{detail}";
        if (string.Equals(
                Environment.GetEnvironmentVariable(RequirePythonTestsEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
            throw new AssertFailedException(message);

        Assert.Inconclusive(message);
        throw new InvalidOperationException(message);
    }

    private static string GetWorkerPath() => GetBuildArtifactPath(
        "Diary.Script.Worker/bin", "Diary.Script.Worker.dll");

    private static string GetAppWorkerPath() => GetBuildArtifactPath(
        "Diary.App/bin", OperatingSystem.IsWindows() ? "Diary.Script.Worker.exe" : "Diary.Script.Worker");

    private static string GetBuildArtifactPath(string projectOutput, string artifactName)
    {
        var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = baseDirectory.Parent?.Name;
        if (configuration is not ("Debug" or "Release"))
            configuration = "Release";

        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../",
            projectOutput,
            configuration,
            "net10.0",
            artifactName));
    }

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
