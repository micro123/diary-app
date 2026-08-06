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
}

public sealed class ScriptManager(
    IScriptBuildService buildService,
    IScriptCatalog catalog,
    IScriptExecutor executor) : IScriptManager
{
    public async ValueTask<ScriptBuildResult> BuildAndRegisterAsync(
        ScriptBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await buildService.BuildAsync(request, cancellationToken);
        if (!result.Succeeded || result.Program is null)
            return result;

        var registration = catalog.Register(result.Program);
        return registration.Succeeded
            ? result
            : new ScriptBuildResult(false, null, result.Diagnostics.AddRange(registration.Diagnostics));
    }

    public ValueTask<ScriptExecutionOutcome> ExecuteAsync(
        string scriptId,
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGet(scriptId, out var program) || program is null)
        {
            return ValueTask.FromResult(new ScriptExecutionOutcome(
                Guid.NewGuid(),
                new ScriptExecutionResult(
                    ScriptExecutionStatus.Rejected,
                    [new ScriptDiagnostic(
                        "SCRIPT_NOT_FOUND",
                        $"No script with ID '{scriptId}' is registered.",
                        ScriptDiagnosticSeverity.Error,
                        ScriptDiagnosticCategory.Runtime)])));
        }

        return executor.ExecuteAsync(program, request, context, timeout, cancellationToken);
    }
}
