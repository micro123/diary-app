using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public interface IScriptManager
{
    ValueTask<ScriptBuildResult> BuildAndRegisterAsync(
        ScriptBuildRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        string scriptId,
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        string scriptId,
        ScriptExecutionRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public sealed class ScriptManager(
    IScriptBuildService buildService,
    IScriptCatalog catalog,
    IScriptExecutor executor,
    IScriptExecutionContextFactory? contextFactory = null,
    IScriptExecutionHistory? history = null,
    IWorkerScriptExecutor? workerExecutor = null) : IScriptManager
{
    public async ValueTask<ScriptBuildResult> BuildAndRegisterAsync(
        ScriptBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await buildService.BuildAsync(request, cancellationToken);
        if (!result.Succeeded || result.Program is null)
            return result;

        var registration = catalog.Register(result.Program);
        if (registration.Succeeded)
            catalog.SetSource(result.Program.Descriptor.Id, new ScriptSourceInfo(
                request.SourcePath,
                request.Source,
                result.EngineName));
        return registration.Succeeded
            ? result
            : new ScriptBuildResult(false, null, result.Diagnostics.AddRange(registration.Diagnostics));
    }

    public async ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        string scriptId,
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGet(scriptId, out var program) || program is null)
        {
            var missing = new ScriptExecutionOutcome(
                Guid.NewGuid(),
                new ScriptExecutionResult(
                    ScriptExecutionStatus.Rejected,
                    [new ScriptDiagnostic(
                        "SCRIPT_NOT_FOUND",
                        $"No script with ID '{scriptId}' is registered.",
                        ScriptDiagnosticSeverity.Error,
                         ScriptDiagnosticCategory.Runtime)]));
            missing = missing with { Source = request.Source };
            Record(scriptId, missing);
            return missing;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = workerExecutor is null
            ? await executor.ExecuteAsync(program, request, context, timeout, cancellationToken)
            : await workerExecutor.ExecuteAsync(
                scriptId,
                request,
                timeout,
                cancellationToken);
        outcome = outcome with { StartedAt = startedAt, Duration = stopwatch.Elapsed, Source = request.Source };
        Record(scriptId, outcome);
        return outcome;
    }

    public async ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        string scriptId,
        ScriptExecutionRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGet(scriptId, out var program) || program is null)
        {
            var missing = new ScriptExecutionOutcome(
                Guid.NewGuid(),
                new ScriptExecutionResult(
                    ScriptExecutionStatus.Rejected,
                    [new ScriptDiagnostic(
                        "SCRIPT_NOT_FOUND",
                        $"No script with ID '{scriptId}' is registered.",
                        ScriptDiagnosticSeverity.Error,
                        ScriptDiagnosticCategory.Runtime)]));
            missing = missing with { Source = request.Source };
            Record(scriptId, missing);
            return missing;
        }

        var executionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var entryKind = ScriptEntryKindResolver.Resolve(request, program.Descriptor);
        var normalizedRequest = request with { EntryKind = entryKind };
        var metadata = new ScriptExecutionMetadata(
            executionId,
            startedAt,
            request.Source,
            scriptId,
            entryKind,
            request.IdempotencyKey,
            request.Preview);
        var context = contextFactory?.Create(metadata, normalizedRequest)
            ?? new ScriptExecutionContext(metadata, normalizedRequest.Target, normalizedRequest.Arguments);
        var outcome = workerExecutor is null
            ? await executor.ExecuteAsync(
                program,
                request,
                context,
                timeout,
                cancellationToken,
                executionId)
            : await workerExecutor.ExecuteAsync(
                scriptId,
                request,
                timeout,
                cancellationToken);
        outcome = outcome with { StartedAt = startedAt, Duration = stopwatch.Elapsed, Source = request.Source };
        Record(scriptId, outcome);
        return outcome;
    }

    private void Record(string scriptId, ScriptExecutionOutcome outcome) => history?.Record(scriptId, outcome);
}
