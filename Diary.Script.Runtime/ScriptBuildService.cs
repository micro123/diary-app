using System.Collections.Immutable;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public interface IScriptBuildService
{
    ValueTask<ScriptBuildResult> BuildAsync(
        ScriptBuildRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class ScriptBuildService(IScriptEngineRegistry engines) : IScriptBuildService
{
    private const ScriptCapability KnownCapabilities =
        ScriptCapability.ReadDiary |
        ScriptCapability.WriteDiary |
        ScriptCapability.UserInteraction |
        ScriptCapability.Clipboard |
        ScriptCapability.Tracker;

    public async ValueTask<ScriptBuildResult> BuildAsync(
        ScriptBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ApiVersion != ScriptApiVersions.Current)
            return Failure("SCRIPT_API_UNSUPPORTED", "The requested script API version is not supported.", request.SourcePath);

        var selection = engines.Select(new ScriptMatchRequest(request.SourcePath));
        if (selection.Engine is null)
            return new ScriptBuildResult(false, null, selection.Diagnostics);

        ScriptBuildResult result;
        try
        {
            result = await selection.Engine.BuildAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("SCRIPT_BUILD_CANCELLED", "The script build was cancelled.", request.SourcePath);
        }
        catch (Exception)
        {
            return MergeFailure(selection.Diagnostics, new ScriptDiagnostic(
                "SCRIPT_ENGINE_BUILD_EXCEPTION",
                "The script engine failed while building the script.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Engine,
                request.SourcePath));
        }

        if (result is null)
            return MergeFailure(selection.Diagnostics, InvalidResult(request.SourcePath));

        result = result with { EngineName = selection.Engine.StableName };

        var buildDiagnostics = result.Diagnostics.IsDefault
            ? ImmutableArray<ScriptDiagnostic>.Empty
            : result.Diagnostics;
        var diagnostics = selection.Diagnostics.AddRange(buildDiagnostics);
        if (!result.Succeeded)
        {
            if (diagnostics.IsEmpty)
            {
                diagnostics = diagnostics.Add(new ScriptDiagnostic(
                    "SCRIPT_BUILD_FAILED",
                    "The script engine did not build the script.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Engine,
                    request.SourcePath));
            }

            if (result.Program is not null)
                diagnostics = diagnostics.Add(InvalidResult(request.SourcePath));
            return new ScriptBuildResult(false, null, diagnostics);
        }

        if (result.Program is null)
            return MergeFailure(diagnostics, InvalidResult(request.SourcePath));

        try
        {
            var descriptor = result.Program.Descriptor;
            if (descriptor is null
                || string.IsNullOrWhiteSpace(descriptor.Id)
                || string.IsNullOrWhiteSpace(descriptor.Name)
                || descriptor.ApiVersion != request.ApiVersion
                || !Enum.IsDefined(descriptor.Scope)
                || (descriptor.Capabilities & ~KnownCapabilities) != 0)
            {
                return MergeFailure(diagnostics, new ScriptDiagnostic(
                    "SCRIPT_DESCRIPTOR_INVALID",
                    "The built script descriptor violates the runtime contract.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation,
                    request.SourcePath));
            }

        }
        catch (Exception)
        {
            return MergeFailure(diagnostics, new ScriptDiagnostic(
                "SCRIPT_DESCRIPTOR_EXCEPTION",
                "The built script descriptor could not be read.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Validation,
                request.SourcePath));
        }

        return result with
        {
            Diagnostics = diagnostics,
            EngineName = selection.Engine.StableName,
        };
    }

    private static ScriptBuildResult Failure(string code, string message, string sourcePath) =>
        ScriptBuildResult.Failure(new ScriptDiagnostic(
            code,
            message,
            ScriptDiagnosticSeverity.Error,
            ScriptDiagnosticCategory.Validation,
            sourcePath));

    private static ScriptBuildResult MergeFailure(
        ImmutableArray<ScriptDiagnostic> diagnostics,
        ScriptDiagnostic diagnostic) =>
        new(false, null, diagnostics.Add(diagnostic));

    private static ScriptDiagnostic InvalidResult(string sourcePath) =>
        new(
            "SCRIPT_BUILD_RESULT_INVALID",
            "The script engine returned an invalid build result.",
            ScriptDiagnosticSeverity.Error,
            ScriptDiagnosticCategory.Engine,
            sourcePath);
}
