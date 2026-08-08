using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptRuntimeTests
{
    private static readonly ScriptExecutionRequest ApplicationRequest =
        new();

    [TestMethod]
    public void EngineRegistry_SelectsHighestPriorityAndRejectsDuplicateStableName()
    {
        var registry = new ScriptEngineRegistry();
        var low = new FakeEngine("low", _ => new(true, 1));
        var high = new FakeEngine("high", _ => new(true, 10));

        Assert.IsTrue(registry.Register(low).Succeeded);
        Assert.IsTrue(registry.Register(high).Succeeded);
        Assert.IsFalse(registry.Register(new FakeEngine("high")).Succeeded);

        var selection = registry.Select(new ScriptMatchRequest("test.fake"));
        Assert.AreSame(high, selection.Engine);
    }

    [TestMethod]
    public void EngineRegistry_ContinuesAfterMatchExceptionAndReportsNoMatch()
    {
        var registry = new ScriptEngineRegistry();
        registry.Register(new FakeEngine("broken", _ => throw new InvalidOperationException("secret")));
        var healthy = new FakeEngine("healthy", _ => new(true, 2));
        registry.Register(healthy);

        var selected = registry.Select(new ScriptMatchRequest("test.fake"));
        Assert.AreSame(healthy, selected.Engine);
        Assert.IsTrue(selected.Diagnostics.Any(item => item.Code == "SCRIPT_ENGINE_MATCH_EXCEPTION"));
        Assert.IsFalse(selected.Diagnostics.Any(item => item.Message.Contains("secret", StringComparison.Ordinal)));

        var empty = new ScriptEngineRegistry().Select(new ScriptMatchRequest("test.unknown"));
        Assert.IsFalse(empty.Succeeded);
        Assert.AreEqual("SCRIPT_ENGINE_NOT_FOUND", empty.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task BuildService_ConvertsFailuresAndExceptionsWithoutPoisoningLaterBuilds()
    {
        var registry = new ScriptEngineRegistry();
        registry.Register(new FakeEngine(
            "fake",
            _ => new(true),
            request => request.Source switch
            {
                "failed" => ValueTask.FromResult(ScriptBuildResult.Failure(Diagnostic("FAKE_BUILD_FAILED"))),
                "throw" => throw new InvalidOperationException("engine secret"),
                _ => ValueTask.FromResult(ScriptBuildResult.Success(new FakeProgram(request.Source))),
            }));
        var service = new ScriptBuildService(registry);

        var failed = await service.BuildAsync(new ScriptBuildRequest("failed.fake", "failed"));
        var thrown = await service.BuildAsync(new ScriptBuildRequest("throw.fake", "throw"));
        var good = await service.BuildAsync(new ScriptBuildRequest("good.fake", "good"));

        Assert.IsFalse(failed.Succeeded);
        Assert.AreEqual("FAKE_BUILD_FAILED", failed.Diagnostics.Single().Code);
        Assert.IsFalse(thrown.Succeeded);
        Assert.AreEqual("SCRIPT_ENGINE_BUILD_EXCEPTION", thrown.Diagnostics.Single().Code);
        Assert.IsFalse(thrown.Diagnostics.Single().Message.Contains("secret", StringComparison.Ordinal));
        Assert.IsTrue(good.Succeeded);
        Assert.AreEqual("good", good.Program!.Descriptor.Id);
    }

    [TestMethod]
    public async Task BuildService_ValidatesApiVersionAndDescriptorContract()
    {
        var registry = new ScriptEngineRegistry();
        registry.Register(new FakeEngine(
            "fake",
            _ => new(true),
            _ => ValueTask.FromResult(ScriptBuildResult.Success(new FakeProgram(
                "bad",
                descriptor: new ScriptDescriptor(
                    "bad",
                    "Bad",
                    (ScriptApiVersion)99,
                     ScriptScope.Application))))));
        var service = new ScriptBuildService(registry);

        var unsupported = await service.BuildAsync(new ScriptBuildRequest(
            "bad.fake",
            "bad",
            (ScriptApiVersion)99));
        var invalidDescriptor = await service.BuildAsync(new ScriptBuildRequest("bad.fake", "bad"));

        Assert.AreEqual("SCRIPT_API_UNSUPPORTED", unsupported.Diagnostics.Single().Code);
        Assert.AreEqual("SCRIPT_DESCRIPTOR_INVALID", invalidDescriptor.Diagnostics.Single().Code);
    }

    [TestMethod]
    public void Catalog_RejectsDuplicateIdAndKeepsOriginalProgram()
    {
        var catalog = new ScriptCatalog();
        var original = new FakeProgram("same");
        var duplicate = new FakeProgram("same");

        Assert.IsTrue(catalog.Register(original).Succeeded);
        var result = catalog.Register(duplicate);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("SCRIPT_ID_DUPLICATE", result.Diagnostics.Single().Code);
        Assert.IsTrue(catalog.TryGet("same", out var stored));
        Assert.AreSame(original, stored);
    }

    [TestMethod]
    public void Catalog_ReplacementAndRemovalDisposePrograms()
    {
        var catalog = new ScriptCatalog();
        var original = new DisposableProgram("replace");
        var replacement = new DisposableProgram("replace");
        catalog.Register(original);

        catalog.RegisterOrReplace(replacement);
        var removed = catalog.Remove("replace");

        Assert.IsTrue(original.Disposed);
        Assert.IsTrue(replacement.Disposed);
        Assert.IsTrue(removed);
    }

    [TestMethod]
    public void ExecutionContext_ExposesRegisteredApisByDefault()
    {
        var readable = new ScriptExecutionContext();
        var api = new FakeReadApi();
        readable.RegisterApi<IFakeReadApi>(api);
        readable.RegisterApi<IFakeWriteApi>(new FakeWriteApi());

        Assert.AreSame(api, readable.GetApi<IFakeReadApi>());
        Assert.IsNotNull(readable.GetApi<IFakeWriteApi>());
        Assert.IsNull(readable.GetApi<object>());
        Assert.IsNull(readable.GetApi<IServiceProvider>());
        Assert.ThrowsExactly<ArgumentException>(() =>
            readable.RegisterApi<IServiceProvider>(new FakeServiceProvider()));
    }

    [TestMethod]
    public async Task Executor_ReturnsSuccessAndUniqueExecutionIds()
    {
        var executor = new ScriptExecutor();
        var program = new FakeProgram("ok");

        var first = await executor.ExecuteAsync(program, ApplicationRequest, EmptyContext());
        var second = await executor.ExecuteAsync(program, ApplicationRequest, EmptyContext());

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, first.Result.Status);
        Assert.AreNotEqual(Guid.Empty, first.ExecutionId);
        Assert.AreNotEqual(first.ExecutionId, second.ExecutionId);
    }

    [TestMethod]
    public async Task Executor_ConvertsExecutionException()
    {
        var program = new FakeProgram(
            "throws",
            (_, _, _) => throw new InvalidOperationException("runtime secret"));

        var outcome = await new ScriptExecutor().ExecuteAsync(
            program,
            ApplicationRequest,
            EmptyContext());

        Assert.AreEqual(ScriptExecutionStatus.Failed, outcome.Result.Status);
        Assert.AreEqual("SCRIPT_EXECUTION_EXCEPTION", outcome.Result.Diagnostics.Single().Code);
        Assert.IsFalse(outcome.Result.Diagnostics.Single().Message.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Executor_SupportsCancellation()
    {
        var program = new FakeProgram("cancel", async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ScriptExecutionResult.Succeeded();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));

        var outcome = await new ScriptExecutor().ExecuteAsync(
            program,
            ApplicationRequest,
            EmptyContext(),
            cancellationToken: cancellation.Token);

        Assert.AreEqual(ScriptExecutionStatus.Cancelled, outcome.Result.Status);
    }

    [TestMethod]
    public async Task Executor_TimeoutStopsWaitingAndObservesLaterFault()
    {
        var completion = new TaskCompletionSource<ScriptExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var program = new FakeProgram("timeout", (_, _, _) => new ValueTask<ScriptExecutionResult>(completion.Task));

        var outcome = await new ScriptExecutor().ExecuteAsync(
            program,
            ApplicationRequest,
            EmptyContext(),
            TimeSpan.FromMilliseconds(20));
        completion.SetException(new InvalidOperationException("late failure"));
        await Task.Yield();

        Assert.AreEqual(ScriptExecutionStatus.TimedOut, outcome.Result.Status);
        Assert.AreEqual("SCRIPT_EXECUTION_TIMED_OUT", outcome.Result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task Executor_RejectsMismatchedTarget()
    {
        var editorRequest = new ScriptExecutionRequest(
            ScriptEditorTarget.ForDay("2026-08-01"));

        var outcome = await new ScriptExecutor().ExecuteAsync(
            new FakeProgram("application"),
            editorRequest,
            EmptyContext());

        Assert.AreEqual(ScriptExecutionStatus.Rejected, outcome.Result.Status);
        Assert.AreEqual("SCRIPT_TARGET_INVALID", outcome.Result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task Executor_ValidatesEditorTargets()
    {
        var editorProgram = new FakeProgram(
            "editor",
            descriptor: new ScriptDescriptor(
                "editor",
                "Editor",
                ScriptApiVersion.V1,
                 ScriptScope.Editor));
        var invalidDay = await new ScriptExecutor().ExecuteAsync(
            editorProgram,
            new ScriptExecutionRequest(new ScriptEditorTarget(
                ScriptEditorTargetKind.Day)),
            EmptyContext());

        var invalidWorkItem = await new ScriptExecutor().ExecuteAsync(
            editorProgram,
            new ScriptExecutionRequest(new ScriptEditorTarget(
                ScriptEditorTargetKind.WorkItem)),
            EmptyContext());

        Assert.AreEqual("SCRIPT_TARGET_INVALID", invalidDay.Result.Diagnostics.Single().Code);
        Assert.AreEqual("SCRIPT_TARGET_INVALID", invalidWorkItem.Result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task Executor_AllowsRegisteredApisByDefault()
    {
        var program = new FakeProgram(
            "read",
            descriptor: new ScriptDescriptor(
                "read",
                "Read",
                ScriptApiVersion.V1,
                 ScriptScope.Application));

        var outcome = await new ScriptExecutor().ExecuteAsync(
            program,
            ApplicationRequest,
            EmptyContext());

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, outcome.Result.Status);
    }

    [TestMethod]
    public async Task Manager_BadScriptDoesNotAffectGoodScript()
    {
        var registry = new ScriptEngineRegistry();
        registry.Register(new FakeEngine(
            "fake",
            _ => new(true),
            request => request.Source == "bad"
                ? ValueTask.FromResult(ScriptBuildResult.Failure(Diagnostic("BAD_SCRIPT")))
                : ValueTask.FromResult(ScriptBuildResult.Success(new FakeProgram("good")))));
        var catalog = new ScriptCatalog();
        var manager = new ScriptManager(new ScriptBuildService(registry), catalog, new ScriptExecutor());

        var bad = await manager.BuildAndRegisterAsync(new ScriptBuildRequest("bad.fake", "bad"));
        var good = await manager.BuildAndRegisterAsync(new ScriptBuildRequest("good.fake", "good"));
        var missing = await manager.ExecuteAsync("bad", ApplicationRequest, EmptyContext());
        var executed = await manager.ExecuteAsync("good", ApplicationRequest, EmptyContext());

        Assert.IsFalse(bad.Succeeded);
        Assert.IsTrue(good.Succeeded);
        Assert.AreEqual(ScriptExecutionStatus.Rejected, missing.Result.Status);
        Assert.AreEqual(ScriptExecutionStatus.Succeeded, executed.Result.Status);
    }

    [TestMethod]
    public async Task Manager_CreatesFreshContextForEachExecution()
    {
        var contexts = new List<IScriptExecutionContext>();
        var registry = new ScriptEngineRegistry();
        registry.Register(new FakeEngine(
            "fake",
            _ => new(true),
            _ => ValueTask.FromResult(ScriptBuildResult.Success(new FakeProgram(
                "fresh",
                (_, context, _) =>
                {
                    contexts.Add(context);
                    return ValueTask.FromResult(ScriptExecutionResult.Succeeded());
                },
                 new ScriptDescriptor("fresh", "Fresh", ScriptApiVersion.V1, ScriptScope.Application))))));
        var catalog = new ScriptCatalog();
        var manager = new ScriptManager(
            new ScriptBuildService(registry),
            catalog,
            new ScriptExecutor(),
             new ScriptExecutionContextFactory((metadata, request) =>
                 new ScriptExecutionContext(metadata, request.Target, request.Arguments)));
        await manager.BuildAndRegisterAsync(new ScriptBuildRequest("fresh.fake", "fresh"));

        await manager.ExecuteAsync("fresh", ApplicationRequest);
        await manager.ExecuteAsync("fresh", ApplicationRequest);

        Assert.AreEqual(2, contexts.Count);
        Assert.AreNotSame(contexts[0], contexts[1]);
    }

    [TestMethod]
    public async Task Manager_UsesWorkerByDefaultAndCanSwitchToInProcess()
    {
        var inProcessExecutions = 0;
        var useInProcess = false;
        var catalog = new ScriptCatalog();
        var program = new FakeProgram(
            "execution-mode",
            (_, _, _) =>
            {
                inProcessExecutions++;
                return ValueTask.FromResult(ScriptExecutionResult.Succeeded());
            });
        Assert.IsTrue(catalog.Register(program).Succeeded);
        catalog.SetSource("execution-mode", new ScriptSourceInfo(
            "execution-mode.fake",
            "same source",
            "fake"));
        var worker = new RecordingWorkerExecutor();
        var manager = new ScriptManager(
            new ScriptBuildService(new ScriptEngineRegistry()),
            catalog,
            new ScriptExecutor(),
            workerExecutor: worker,
            executionModeProvider: new ScriptExecutionModeProvider(() => useInProcess));

        var workerOutcome = await manager.ExecuteAsync("execution-mode", ApplicationRequest, EmptyContext());

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, workerOutcome.Result.Status);
        Assert.AreEqual(1, worker.Calls);
        Assert.AreEqual(0, inProcessExecutions);

        useInProcess = true;
        var inProcessOutcome = await manager.ExecuteAsync("execution-mode", ApplicationRequest, EmptyContext());

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, inProcessOutcome.Result.Status);
        Assert.AreEqual(1, worker.Calls);
        Assert.AreEqual(1, inProcessExecutions);
        Assert.IsNull(inProcessOutcome.WorkerId);
    }

    [TestMethod]
    public async Task Manager_FallsBackToWorkerForProgramsWithoutInProcessSupport()
    {
        var catalog = new ScriptCatalog();
        var program = new WorkerOnlyProgram("worker-only");
        Assert.IsTrue(catalog.Register(program).Succeeded);
        catalog.SetSource("worker-only", new ScriptSourceInfo(
            "worker-only.py",
            "same source",
            "python"));
        var worker = new RecordingWorkerExecutor();
        var manager = new ScriptManager(
            new ScriptBuildService(new ScriptEngineRegistry()),
            catalog,
            new ScriptExecutor(),
            workerExecutor: worker,
            executionModeProvider: new ScriptExecutionModeProvider(() => true));

        var outcome = await manager.ExecuteAsync("worker-only", ApplicationRequest, EmptyContext());

        Assert.AreEqual(ScriptExecutionStatus.Succeeded, outcome.Result.Status);
        Assert.AreEqual(1, worker.Calls);
    }

    [TestMethod]
    public async Task Manager_RecordsDurationAndSanitizesHistory()
    {
        var registry = new ScriptEngineRegistry();
        registry.Register(new FakeEngine(
            "fake",
            _ => new(true),
            _ => ValueTask.FromResult(ScriptBuildResult.Success(new FakeProgram(
                "history",
                (_, _, _) => ValueTask.FromResult(new ScriptExecutionResult(
                    ScriptExecutionStatus.Failed,
                    [new ScriptDiagnostic(
                        "FAILURE",
                        "token=super-secret",
                        ScriptDiagnosticSeverity.Error,
                        ScriptDiagnosticCategory.Runtime)])))))));
        var history = new ScriptExecutionHistory();
        var manager = new ScriptManager(
            new ScriptBuildService(registry),
            new ScriptCatalog(),
            new ScriptExecutor(),
             new ScriptExecutionContextFactory((metadata, request) =>
                 new ScriptExecutionContext(metadata, request.Target, request.Arguments)),
            history);
        await manager.BuildAndRegisterAsync(new ScriptBuildRequest("history.fake", "history"));

        var outcome = await manager.ExecuteAsync(
            "history",
            new ScriptExecutionRequest(
                Source: ScriptExecutionSource.Manual));
        var entry = history.GetRecent(1).Single();

        Assert.AreEqual(ScriptExecutionStatus.Failed, outcome.Result.Status);
        Assert.IsNotNull(entry.Outcome.StartedAt);
        Assert.IsTrue(entry.Outcome.Duration >= TimeSpan.Zero);
        StringAssert.Contains(entry.Outcome.Result.Diagnostics.Single().Message, "<redacted>");
        Assert.IsFalse(entry.Outcome.Result.Diagnostics.Single().Message.Contains("super-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void History_PreservesWorkerCorrelationFields()
    {
        var history = new ScriptExecutionHistory();
        var outcome = new ScriptExecutionOutcome(
            Guid.NewGuid(),
            ScriptExecutionResult.Succeeded(),
            WorkerId: "worker-1",
            WorkerRequestId: "request-1");

        history.Record("demo", outcome);

        var recorded = history.GetRecent(1).Single().Outcome;
        Assert.AreEqual("worker-1", recorded.WorkerId);
        Assert.AreEqual("request-1", recorded.WorkerRequestId);
    }

    [TestMethod]
    public void History_StoresSanitizedEntriesInMemory()
    {
        var history = new ScriptExecutionHistory();
        history.Record("demo", new ScriptExecutionOutcome(
            Guid.NewGuid(),
            new ScriptExecutionResult(ScriptExecutionStatus.Failed, [new ScriptDiagnostic(
                    "FAILURE", "token=super-secret", ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Runtime)]),
            WorkerId: "worker-1",
            WorkerRequestId: "request-1"));

        var entry = history.GetRecent(1).Single();

        Assert.AreEqual("worker-1", entry.Outcome.WorkerId);
        Assert.AreEqual("request-1", entry.Outcome.WorkerRequestId);
        StringAssert.Contains(entry.Outcome.Result.Diagnostics.Single().Message, "<redacted>");
        Assert.IsFalse(entry.Outcome.Result.Diagnostics.Single().Message.Contains("super-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void History_LimitsToThirtyEntriesAndCanClear()
    {
        var history = new ScriptExecutionHistory();
        for (var index = 0; index < 35; index++)
            history.Record($"script-{index}", new ScriptExecutionOutcome(
                Guid.NewGuid(), ScriptExecutionResult.Succeeded()));

        Assert.AreEqual(30, history.GetRecent().Count);
        Assert.AreEqual("script-34", history.GetRecent(1).Single().ScriptId);
        history.Clear();
        Assert.AreEqual(0, history.GetRecent().Count);
    }

    private static ScriptExecutionContext EmptyContext() => new();

    private static ScriptDiagnostic Diagnostic(string code) =>
        new(code, code, ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Engine);

    private interface IFakeReadApi;
    private interface IFakeWriteApi;
    private sealed class FakeReadApi : IFakeReadApi;
    private sealed class FakeWriteApi : IFakeWriteApi;
    private sealed class FakeServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class DisposableProgram(string id) : IScriptProgramV1, IDisposable
    {
        public bool Disposed { get; private set; }
        public ScriptDescriptor Descriptor { get; } = new(
            id,
            id,
            ScriptApiVersion.V1,
             ScriptScope.Application);

        public ValueTask<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptExecutionResult.Succeeded());

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeEngine(
        string stableName,
        Func<ScriptMatchRequest, ScriptMatchResult>? match = null,
        Func<ScriptBuildRequest, ValueTask<ScriptBuildResult>>? build = null) : IScriptEngineV1
    {
        public string Name => stableName;
        public string StableName => stableName;
        public string Version => "1.0";

        public ScriptMatchResult Match(ScriptMatchRequest request) =>
            match?.Invoke(request) ?? new ScriptMatchResult(false);

        public ValueTask<ScriptBuildResult> BuildAsync(
            ScriptBuildRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return build?.Invoke(request)
                ?? ValueTask.FromResult(ScriptBuildResult.Success(new FakeProgram(request.Source)));
        }
    }

    private sealed class FakeProgram : IInProcessScriptProgram
    {
        private readonly Func<ScriptExecutionRequest, IScriptExecutionContext, CancellationToken,
            ValueTask<ScriptExecutionResult>> _execute;

        public FakeProgram(
            string id,
            Func<ScriptExecutionRequest, IScriptExecutionContext, CancellationToken,
                ValueTask<ScriptExecutionResult>>? execute = null,
            ScriptDescriptor? descriptor = null)
        {
            Descriptor = descriptor ?? new ScriptDescriptor(
                id,
                id,
                ScriptApiVersion.V1,
                 ScriptScope.Application);
            _execute = execute ?? ((_, _, _) => ValueTask.FromResult(ScriptExecutionResult.Succeeded()));
        }

        public ScriptDescriptor Descriptor { get; }

        public ValueTask<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            CancellationToken cancellationToken = default) =>
            _execute(request, context, cancellationToken);
    }

    private sealed class WorkerOnlyProgram(string id) : IScriptProgramV1
    {
        public ScriptDescriptor Descriptor { get; } = new(
            id,
            id,
            ScriptApiVersion.V1,
            ScriptScope.Application);

        public ValueTask<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ScriptExecutionResult.Succeeded());
    }

    private sealed class RecordingWorkerExecutor : IWorkerScriptExecutor
    {
        public int Calls { get; private set; }

        public ValueTask<ScriptExecutionOutcome> ExecuteAsync(
            string scriptId,
            ScriptExecutionRequest request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new ScriptExecutionOutcome(
                Guid.NewGuid(),
                ScriptExecutionResult.Succeeded(),
                WorkerId: "test-worker",
                WorkerRequestId: "test-request"));
        }
    }
}
