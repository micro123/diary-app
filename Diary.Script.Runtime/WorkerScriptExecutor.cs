using System.Collections.Immutable;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public interface IWorkerScriptExecutor
{
    ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        string scriptId,
        ScriptExecutionRequest request,
        ScriptCapability grantedCapabilities,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public sealed class WorkerScriptExecutor(
    IScriptCatalog catalog,
    WorkerSupervisor supervisor,
    WorkerHandshakeOptions handshakeOptions) : IWorkerScriptExecutor
{
    public async ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        string scriptId,
        ScriptExecutionRequest request,
        ScriptCapability grantedCapabilities,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGetSource(scriptId, out var source) || source is null)
            return Failed(scriptId, request.Source, "SCRIPT_SOURCE_NOT_FOUND", "找不到脚本源码。");
        try
        {
            if (supervisor.State == WorkerState.Stopped || supervisor.State == WorkerState.Failed)
                await supervisor.StartAsync(handshakeOptions, cancellationToken);
            var executionId = Guid.NewGuid();
            var result = await supervisor.ExecuteAsync(
                scriptId,
                executionId.ToString(),
                new WorkerExecutePayload(scriptId, source.SourcePath, source.Source, request),
                timeout,
                grantedCapabilities,
                cancellationToken);
            return new(executionId, new ScriptExecutionResult(
                result.Payload.Status,
                result.Payload.Diagnostics.ToImmutableArray()), Source: request.Source,
                Duration: TimeSpan.FromMilliseconds(result.Payload.DurationMilliseconds));
        }
        catch (Exception exception)
        {
            return Failed(scriptId, request.Source, "WORKER_EXECUTION_FAILED", exception.Message);
        }
    }

    private static ScriptExecutionOutcome Failed(string scriptId, ScriptExecutionSource source, string code, string message) =>
        new(Guid.NewGuid(), new ScriptExecutionResult(ScriptExecutionStatus.Failed, [new ScriptDiagnostic(
            code, message, ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime)]), Source: source);
}

public sealed record WorkerExecutePayload(
    string ScriptId,
    string SourcePath,
    string Source,
    ScriptExecutionRequest Request);
