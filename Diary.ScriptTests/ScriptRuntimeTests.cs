using Diary.Script.Runtime;
using Diary.ScriptBase;

namespace Diary.ScriptTests;

[TestClass]
public sealed class ScriptRuntimeTests
{
    private static readonly ScriptExecutionRequest ApplicationRequest =
        new(new ScriptTarget(ScriptScope.Application));

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
                    ScriptScope.Application,
                    ScriptCapability.None))))));
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
    public void ExecutionContext_ExposesOnlyRegisteredAndPermittedApis()
    {
        var readable = new ScriptExecutionContext(ScriptCapability.ReadDiary);
        var api = new FakeReadApi();
        readable.RegisterApi<IFakeReadApi>(api, ScriptCapability.ReadDiary);
        readable.RegisterApi<IFakeWriteApi>(new FakeWriteApi(), ScriptCapability.WriteDiary);

        Assert.AreSame(api, readable.GetApi<IFakeReadApi>());
        Assert.IsNull(readable.GetApi<IFakeWriteApi>());
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
            new ScriptTarget(ScriptScope.Editor, new EditorScriptContext("2026-08-01", "2026-08-01")));

        var outcome = await new ScriptExecutor().ExecuteAsync(
            new FakeProgram("application"),
            editorRequest,
            EmptyContext());

        Assert.AreEqual(ScriptExecutionStatus.Rejected, outcome.Result.Status);
        Assert.AreEqual("SCRIPT_TARGET_INVALID", outcome.Result.Diagnostics.Single().Code);
    }

    [TestMethod]
    public async Task Executor_RejectsCapabilitiesNotGrantedByContext()
    {
        var program = new FakeProgram(
            "read",
            descriptor: new ScriptDescriptor(
                "read",
                "Read",
                ScriptApiVersion.V1,
                ScriptScope.Application,
                ScriptCapability.ReadDiary));

        var outcome = await new ScriptExecutor().ExecuteAsync(
            program,
            ApplicationRequest,
            EmptyContext());

        Assert.AreEqual(ScriptExecutionStatus.Rejected, outcome.Result.Status);
        Assert.AreEqual("SCRIPT_CAPABILITY_DENIED", outcome.Result.Diagnostics.Single().Code);
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

    private static ScriptExecutionContext EmptyContext() => new(ScriptCapability.None);

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

    private sealed class FakeProgram : IScriptProgramV1
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
                ScriptScope.Application,
                ScriptCapability.None);
            _execute = execute ?? ((_, _, _) => ValueTask.FromResult(ScriptExecutionResult.Succeeded()));
        }

        public ScriptDescriptor Descriptor { get; }

        public ValueTask<ScriptExecutionResult> ExecuteAsync(
            ScriptExecutionRequest request,
            IScriptExecutionContext context,
            CancellationToken cancellationToken = default) =>
            _execute(request, context, cancellationToken);
    }
}
