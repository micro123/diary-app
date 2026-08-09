using System.Collections.Immutable;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public interface IWorkerScriptExecutor
{
    ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        string scriptId,
        ScriptExecutionRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public sealed class WorkerScriptExecutor(
    IScriptCatalog catalog,
    IReadOnlyDictionary<string, WorkerRuntime> runtimes) : IWorkerScriptExecutor
{
    public async ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        string scriptId,
        ScriptExecutionRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGetSource(scriptId, out var source) || source is null)
            return Failed(scriptId, request.Source, "SCRIPT_SOURCE_NOT_FOUND", "找不到脚本源码。");
        if (!catalog.TryGet(scriptId, out var program) || program is null)
            return Failed(scriptId, request.Source, "SCRIPT_NOT_FOUND", "找不到脚本程序。");
        var engineName = source.EngineName ?? "csharp";
        if (!runtimes.TryGetValue(engineName, out var runtime))
            return Failed(scriptId, request.Source, "SCRIPT_WORKER_NOT_FOUND", $"找不到脚本引擎 '{engineName}' 的 Worker。");

        try
        {
            if (runtime.Supervisor.State == WorkerState.Stopped || runtime.Supervisor.State == WorkerState.Failed)
                await runtime.Supervisor.StartAsync(runtime.HandshakeOptions, cancellationToken);
            var executionId = Guid.NewGuid();
            var descriptor = program.Descriptor;
            var result = await runtime.Supervisor.ExecuteAsync(
                scriptId,
                executionId.ToString(),
                new WorkerExecutePayload(
                    scriptId,
                    source.SourcePath,
                    source.Source,
                    request,
                    new ScriptDescriptorHint(
                        descriptor.Id,
                        descriptor.Name,
                         descriptor.Scope,
                         descriptor.Description,
                         engineName,
                         descriptor.SupportedEditorTargets,
                         descriptor.EntryKind)),
                timeout,
                cancellationToken);
            return new(executionId, new ScriptExecutionResult(
                result.Payload.Status,
                result.Payload.Diagnostics.ToImmutableArray(),
                result.Payload.Effects), Source: request.Source,
                WorkerId: runtime.Supervisor.WorkerId, WorkerRequestId: result.RequestId,
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
    ScriptExecutionRequest Request,
    ScriptDescriptorHint? DescriptorHint = null);

public sealed record WorkerRuntime(
    string EngineName,
    WorkerSupervisor Supervisor,
    WorkerHandshakeOptions HandshakeOptions);
