namespace Diary.ScriptBase;

public sealed class LegacyScriptEngineAdapter(IScriptEngine engine) : IScriptEngineV1
{
    public string Name => engine.Name;
    public string Version => "legacy";

    public ScriptMatchResult Match(ScriptMatchRequest request) =>
        new(engine.Match(request.SourcePath));

    public ValueTask<ScriptBuildResult> BuildAsync(
        ScriptBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ApiVersion != ScriptApiVersion.V1)
        {
            return ValueTask.FromResult(ScriptBuildResult.Failure(new ScriptDiagnostic(
                "SCRIPT_API_UNSUPPORTED",
                $"Unsupported script API version: {request.ApiVersion}",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Engine,
                request.SourcePath)));
        }

        if (!engine.Build(request.Source, out var script))
        {
            return ValueTask.FromResult(ScriptBuildResult.Failure(new ScriptDiagnostic(
                "LEGACY_BUILD_FAILED",
                "The legacy script engine could not build the script.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Engine,
                request.SourcePath)));
        }

        IScriptProgramV1 program = new LegacyScriptProgramAdapter(request.SourcePath, script);
        return ValueTask.FromResult(ScriptBuildResult.Success(program));
    }
}

public sealed class LegacyScriptProgramAdapter : IScriptProgramV1
{
    private readonly IScript _script;

    public LegacyScriptProgramAdapter(string sourcePath, IScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        _script = script;
        Descriptor = new ScriptDescriptor(
            sourcePath,
            Path.GetFileNameWithoutExtension(sourcePath),
            ScriptApiVersion.V1,
            script.Usage == ScriptUsage.Editor ? ScriptScope.Editor : ScriptScope.Application,
            "Legacy script compatibility adapter");
    }

    public ScriptDescriptor Descriptor { get; }

    public ValueTask<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var api = context.GetApi<IScriptApi>();
        if (api is null)
            return ValueTask.FromResult(Failed("LEGACY_API_UNAVAILABLE", "The legacy script API is unavailable."));

        try
        {
            Execute(request, api);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ScriptExecutionResult.Succeeded());
        }
        catch (OperationCanceledException)
        {
            return ValueTask.FromResult(ScriptExecutionResult.Cancelled());
        }
        catch (Exception)
        {
            return ValueTask.FromResult(Failed("LEGACY_EXECUTION_FAILED", "The legacy script failed."));
        }
    }

    private void Execute(ScriptExecutionRequest request, IScriptApi api)
    {
        if (_script is IApplicationScript application && request.Target is null)
        {
            application.Execute(api);
            return;
        }

        if (_script is IEditorScript editor && request.Target is { } target)
        {
            var range = ScriptEditorTargetResolver.GetDateRange(target);
            var startDate = range?.StartDate ?? target.WorkItem?.Date;
            var endDate = range?.EndDate ?? target.WorkItem?.Date;
            if (startDate is null || endDate is null)
                throw new InvalidOperationException("The legacy editor script target has no date.");
            if (startDate == endDate && editor.ApplyToDay)
                editor.ExecuteDay(startDate, api);
            else if (editor.ApplyToRange)
                editor.ExecuteRange(startDate, endDate, api);
            else
                throw new InvalidOperationException("The legacy editor script does not support this target.");
            return;
        }

        throw new InvalidOperationException("The script and execution target do not match.");
    }

    private static ScriptExecutionResult Failed(string code, string message) =>
        new(ScriptExecutionStatus.Failed,
        [
            new ScriptDiagnostic(
                code,
                message,
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Runtime),
        ]);
}
