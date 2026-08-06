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

        if (!IsValidTarget(descriptor, request.Target))
            return Rejected(actualExecutionId, "SCRIPT_TARGET_INVALID", "The execution target does not match the script descriptor.");
        if ((descriptor.Capabilities & ~context.Capabilities) != 0)
            return Rejected(actualExecutionId, "SCRIPT_CAPABILITY_DENIED", "The script requests capabilities that are not granted.");

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

    private static bool IsValidTarget(ScriptDescriptor? descriptor, ScriptTarget? target)
    {
        if (descriptor is null || target is null || descriptor.Scope != target.Scope)
            return false;
        if (!IsValidBusinessTarget(target.Business))
            return false;
        return target.Scope switch
        {
            ScriptScope.Application => target.Editor is null,
            ScriptScope.Editor => IsValidEditorTarget(target.Editor),
            _ => false,
        };
    }

    private static bool IsValidEditorTarget(EditorScriptContext? editor)
    {
        if (editor is null
            || !DateOnly.TryParseExact(editor.StartDate, "yyyy-MM-dd", out var start)
            || !DateOnly.TryParseExact(editor.EndDate, "yyyy-MM-dd", out var end)
            || start > end)
        {
            return false;
        }

        return editor.Granularity switch
        {
            ScriptTimeGranularity.Custom => true,
            ScriptTimeGranularity.Day => start == end,
            ScriptTimeGranularity.Week => start.DayOfWeek == DayOfWeek.Monday && end == start.AddDays(6),
            ScriptTimeGranularity.Month => start.Day == 1 && end.Year == start.Year && end.Month == start.Month
                && end.Day == DateTime.DaysInMonth(end.Year, end.Month),
            ScriptTimeGranularity.Quarter => start.Day == 1 && (start.Month is 1 or 4 or 7 or 10)
                && end == start.AddMonths(3).AddDays(-1),
            ScriptTimeGranularity.Year => start.Month == 1 && start.Day == 1 && end == new DateOnly(start.Year, 12, 31),
            _ => false,
        };
    }

    private static bool IsValidBusinessTarget(ScriptBusinessTarget? target)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.TargetId))
            return target is null;
        if (!Enum.IsDefined(target.Kind))
            return false;
        var trackerTarget = target.Kind is ScriptBusinessTargetKind.TrackerIssue or ScriptBusinessTargetKind.TrackerInstance;
        return !trackerTarget || (!string.IsNullOrWhiteSpace(target.PluginId)
            && !string.IsNullOrWhiteSpace(target.InstanceId));
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
