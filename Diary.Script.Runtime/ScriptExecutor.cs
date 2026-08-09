using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public sealed record ScriptExecutionOutcome(
    Guid ExecutionId,
    ScriptExecutionResult Result,
    DateTimeOffset? StartedAt = null,
    TimeSpan Duration = default,
    ScriptExecutionSource Source = ScriptExecutionSource.Unknown,
    string? WorkerId = null,
    string? WorkerRequestId = null);

public interface IScriptExecutor
{
    ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        IScriptProgramV1 program,
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        Guid? executionId = null);
}

public sealed class ScriptExecutor : IScriptExecutor
{
    public async ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        IScriptProgramV1 program,
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        Guid? executionId = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var actualExecutionId = executionId ?? Guid.NewGuid();

        if (cancellationToken.IsCancellationRequested)
            return new(actualExecutionId, ScriptExecutionResult.Cancelled());
        if (timeout is { } invalidTimeout && invalidTimeout <= TimeSpan.Zero)
            return Rejected(actualExecutionId, "SCRIPT_TIMEOUT_INVALID", "The execution timeout must be positive.");

        ScriptDescriptor descriptor;
        try
        {
            descriptor = program.Descriptor;
        }
        catch (Exception)
        {
            return Rejected(actualExecutionId, "SCRIPT_DESCRIPTOR_EXCEPTION", "The script descriptor could not be read.");
        }

        var entryKind = ScriptEntryKindResolver.Resolve(request, descriptor);
        if (!ScriptEntryKindResolver.IsCompatible(entryKind, descriptor.Scope))
            return Rejected(actualExecutionId, "SCRIPT_ENTRY_KIND_MISMATCH", "The execution entry does not match the script descriptor.");
        if (!IsValidTarget(entryKind, request.Target, out var targetError))
            return Rejected(actualExecutionId, "SCRIPT_TARGET_INVALID", "The execution target does not match the script entry.");
        request = request with { EntryKind = entryKind };
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ScriptExecutionResult> executionTask;
        try
        {
            executionTask = program.ExecuteAsync(request, context, executionCancellation.Token).AsTask();
        }
        catch (OperationCanceledException)
        {
            return new(actualExecutionId, ScriptExecutionResult.Cancelled());
        }
        catch (Exception)
        {
            return Failed(actualExecutionId);
        }

        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var timeoutTask = timeout is null
            ? Task.Delay(Timeout.InfiniteTimeSpan)
            : Task.Delay(timeout.Value);
        var completed = await Task.WhenAny(executionTask, cancellationTask, timeoutTask);
        if (completed == executionTask)
        {
            try
            {
                var result = await executionTask;
                return result is null ? Failed(actualExecutionId) : new(actualExecutionId, Normalize(result));
            }
            catch (OperationCanceledException)
            {
                return new(actualExecutionId, ScriptExecutionResult.Cancelled());
            }
            catch (Exception)
            {
                return Failed(actualExecutionId);
            }
        }

        executionCancellation.Cancel();
        ObserveFault(executionTask);
        if (completed == cancellationTask)
            return new(actualExecutionId, ScriptExecutionResult.Cancelled());

        return new(actualExecutionId, new ScriptExecutionResult(
            ScriptExecutionStatus.TimedOut,
            [RuntimeDiagnostic("SCRIPT_EXECUTION_TIMED_OUT", "The script execution timed out.")]));
    }

    private static bool IsValidTarget(
        ScriptEntryKind entryKind,
        ScriptEditorTarget? target,
        out string error)
    {
        error = string.Empty;
        if (entryKind != ScriptEntryKind.Editor)
        {
            error = target is null ? string.Empty : "非编辑器脚本不能提供编辑器目标。";
            return target is null;
        }

        return ScriptEditorTargetResolver.TryValidate(target, out _, out error);
    }

    private static ScriptExecutionResult Normalize(ScriptExecutionResult result) =>
        ScriptDiagnosticSanitizer.Sanitize(result);

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private static ScriptExecutionOutcome Rejected(Guid executionId, string code, string message) =>
        new(executionId, new ScriptExecutionResult(
            ScriptExecutionStatus.Rejected,
            [RuntimeDiagnostic(code, message)]));

    private static ScriptExecutionOutcome Failed(Guid executionId) =>
        new(executionId, new ScriptExecutionResult(
            ScriptExecutionStatus.Failed,
            [RuntimeDiagnostic("SCRIPT_EXECUTION_EXCEPTION", "The script failed during execution.")]));

    private static ScriptDiagnostic RuntimeDiagnostic(string code, string message) =>
        new(code, message, ScriptDiagnosticSeverity.Error, ScriptDiagnosticCategory.Runtime);
}
