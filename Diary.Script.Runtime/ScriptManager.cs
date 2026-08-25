using Diary.ScriptBase;
using System.Collections.Immutable;

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

        var normalizedRequest = BindRequest(scriptId, program.Descriptor, request, out var rejected);
        if (rejected is not null)
        {
            Record(scriptId, rejected);
            return rejected;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var boundContext = new BoundScriptExecutionContext(context, normalizedRequest!);
        var outcome = workerExecutor is null
            ? await executor.ExecuteAsync(program, normalizedRequest!, boundContext, timeout, cancellationToken)
            : await workerExecutor.ExecuteAsync(
                scriptId,
                normalizedRequest!,
                timeout,
                cancellationToken,
                context.Metadata?.ExecutionId);
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

        var boundRequest = BindRequest(scriptId, program.Descriptor, request, out var rejected);
        if (rejected is not null)
        {
            Record(scriptId, rejected);
            return rejected;
        }

        var executionId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var entryKind = ScriptEntryKindResolver.Resolve(boundRequest!, program.Descriptor);
        var normalizedRequest = boundRequest! with { EntryKind = entryKind };
        var metadata = new ScriptExecutionMetadata(
            executionId,
            startedAt,
            request.Source,
            scriptId,
            entryKind,
            request.IdempotencyKey,
            request.Preview);
        var context = contextFactory?.Create(metadata, normalizedRequest)
            ?? new ScriptExecutionContext(
                metadata,
                normalizedRequest.Target,
                normalizedRequest.Arguments,
                automation: ScriptAutomationContextFactory.FromRequest(normalizedRequest));
        var outcome = workerExecutor is null
            ? await executor.ExecuteAsync(
                program,
                normalizedRequest,
                context,
                timeout,
                cancellationToken,
                executionId)
            : await workerExecutor.ExecuteAsync(
                scriptId,
                normalizedRequest,
                timeout,
                cancellationToken);
        outcome = outcome with { StartedAt = startedAt, Duration = stopwatch.Elapsed, Source = request.Source };
        Record(scriptId, outcome);
        return outcome;
    }

    private void Record(string scriptId, ScriptExecutionOutcome outcome) => history?.Record(scriptId, outcome);

    private ScriptExecutionRequest? BindRequest(
        string scriptId,
        ScriptDescriptor descriptor,
        ScriptExecutionRequest request,
        out ScriptExecutionOutcome? rejected)
    {
        catalog.TryGetSource(scriptId, out var source);
        IReadOnlyDictionary<string, string>? suppliedArguments = request.Arguments;
        if (descriptor.ApiVersion == ScriptApiVersion.V1 && request.AutomationEventData is { Count: > 0 })
        {
            var legacyArguments = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            if (request.Arguments is not null)
            {
                foreach (var pair in request.Arguments)
                    legacyArguments[pair.Key] = pair.Value;
            }
            foreach (var pair in request.AutomationEventData)
                legacyArguments[pair.Key] = pair.Value;
            suppliedArguments = legacyArguments.ToImmutable();
        }

        var binding = ScriptParameterBinder.Bind(
            descriptor,
            source?.DefaultArguments,
            suppliedArguments,
            source?.SourcePath);
        if (!binding.Succeeded)
        {
            rejected = new ScriptExecutionOutcome(
                Guid.NewGuid(),
                new ScriptExecutionResult(ScriptExecutionStatus.Rejected, binding.Diagnostics),
                Source: request.Source);
            return null;
        }

        rejected = null;
        return request with
        {
            Arguments = binding.Arguments,
            AutomationEventData = descriptor.ApiVersion == ScriptApiVersion.V2
                ? request.AutomationEventData ?? ImmutableDictionary<string, string>.Empty
                : request.AutomationEventData,
        };
    }
}
