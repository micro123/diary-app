using System.Collections.Immutable;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public sealed record ScriptExecutionOutcome(Guid ExecutionId, ScriptExecutionResult Result);

public interface IScriptExecutor
{
    ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        IScriptProgramV1 program,
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

public sealed class ScriptExecutor : IScriptExecutor
{
    public async ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        IScriptProgramV1 program,
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var executionId = Guid.NewGuid();

        if (cancellationToken.IsCancellationRequested)
            return new(executionId, ScriptExecutionResult.Cancelled());
        if (timeout is { } invalidTimeout && invalidTimeout <= TimeSpan.Zero)
            return Rejected(executionId, "SCRIPT_TIMEOUT_INVALID", "The execution timeout must be positive.");

        ScriptDescriptor descriptor;
        try
        {
            descriptor = program.Descriptor;
        }
        catch (Exception)
        {
            return Rejected(executionId, "SCRIPT_DESCRIPTOR_EXCEPTION", "The script descriptor could not be read.");
        }

        if (!IsValidTarget(descriptor, request.Target))
            return Rejected(executionId, "SCRIPT_TARGET_INVALID", "The execution target does not match the script descriptor.");

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ScriptExecutionResult> executionTask;
        try
        {
            executionTask = program.ExecuteAsync(request, context, executionCancellation.Token).AsTask();
        }
        catch (OperationCanceledException)
        {
            return new(executionId, ScriptExecutionResult.Cancelled());
        }
        catch (Exception)
        {
            return Failed(executionId);
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
                return result is null ? Failed(executionId) : new(executionId, Normalize(result));
            }
            catch (OperationCanceledException)
            {
                return new(executionId, ScriptExecutionResult.Cancelled());
            }
            catch (Exception)
            {
                return Failed(executionId);
            }
        }

        executionCancellation.Cancel();
        ObserveFault(executionTask);
        if (completed == cancellationTask)
            return new(executionId, ScriptExecutionResult.Cancelled());

        return new(executionId, new ScriptExecutionResult(
            ScriptExecutionStatus.TimedOut,
            [RuntimeDiagnostic("SCRIPT_EXECUTION_TIMED_OUT", "The script execution timed out.")]));
    }

    private static bool IsValidTarget(ScriptDescriptor? descriptor, ScriptTarget? target)
    {
        if (descriptor is null || target is null || descriptor.Scope != target.Scope)
            return false;
        return target.Scope switch
        {
            ScriptScope.Application => target.Editor is null,
            ScriptScope.Editor => target.Editor is { StartDate.Length: > 0, EndDate.Length: > 0 },
            _ => false,
        };
    }

    private static ScriptExecutionResult Normalize(ScriptExecutionResult result) =>
        result.Diagnostics.IsDefault ? result with { Diagnostics = ImmutableArray<ScriptDiagnostic>.Empty } : result;

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
