using System.Collections.Immutable;
using System.Text.Json;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public sealed record ScriptFileMetadata(
    bool Enabled = true,
    ScriptApiVersion ApiVersion = ScriptApiVersion.V1);

public sealed record ScriptDirectoryEntry(
    string SourcePath,
    ScriptScope Scope,
    bool Enabled,
    ScriptBuildResult? BuildResult = null);

public sealed record ScriptDirectoryLoadResult(
    ImmutableArray<ScriptDirectoryEntry> Entries,
    ImmutableArray<ScriptDiagnostic> Diagnostics);

public interface IScriptDirectoryLoader
{
    ValueTask<ScriptDirectoryLoadResult> LoadAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class ScriptDirectoryLoader(
    IScriptEngineRegistry engines,
    IScriptBuildService buildService,
    IScriptCatalog catalog) : IScriptDirectoryLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async ValueTask<ScriptDirectoryLoadResult> LoadAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var entries = ImmutableArray.CreateBuilder<ScriptDirectoryEntry>();
        var diagnostics = ImmutableArray.CreateBuilder<ScriptDiagnostic>();

        foreach (var (directoryName, scope) in new[]
                 {
                     ("application", ScriptScope.Application),
                     ("editor", ScriptScope.Editor),
                 })
        {
            var directory = Path.Combine(rootDirectory, directoryName);
            Directory.CreateDirectory(directory);
            foreach (var sourcePath in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sourcePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || engines.Select(new ScriptMatchRequest(sourcePath)).Engine is null)
                {
                    continue;
                }

                var metadata = await ReadMetadataAsync(sourcePath, diagnostics, cancellationToken);
                if (metadata is null)
                {
                    entries.Add(new ScriptDirectoryEntry(sourcePath, scope, false));
                    continue;
                }

                if (!metadata.Enabled)
                {
                    entries.Add(new ScriptDirectoryEntry(sourcePath, scope, false));
                    continue;
                }

                ScriptBuildResult result;
                try
                {
                    var source = await File.ReadAllTextAsync(sourcePath, cancellationToken);
                    result = await buildService.BuildAsync(
                        new ScriptBuildRequest(sourcePath, source, metadata.ApiVersion),
                        cancellationToken);
                }
                catch (IOException)
                {
                    result = Failure(
                        "SCRIPT_SOURCE_READ_FAILED",
                        "The script source could not be read.",
                        sourcePath);
                }
                catch (UnauthorizedAccessException)
                {
                    result = Failure(
                        "SCRIPT_SOURCE_READ_FAILED",
                        "The script source could not be read.",
                        sourcePath);
                }

                if (result.Succeeded && result.Program is not null && result.Program.Descriptor.Scope != scope)
                {
                    result = new ScriptBuildResult(
                        false,
                        null,
                        result.Diagnostics.Add(new ScriptDiagnostic(
                            "SCRIPT_SCOPE_MISMATCH",
                            $"The script scope must be '{scope}' for this directory.",
                            ScriptDiagnosticSeverity.Error,
                            ScriptDiagnosticCategory.Validation,
                            sourcePath)));
                }

                if (result.Succeeded && result.Program is not null)
                {
                    var registration = catalog.Register(result.Program);
                    if (!registration.Succeeded)
                        result = new ScriptBuildResult(false, null, result.Diagnostics.AddRange(registration.Diagnostics));
                }

                entries.Add(new ScriptDirectoryEntry(sourcePath, scope, true, result));
                diagnostics.AddRange(result.Diagnostics);
            }
        }

        return new ScriptDirectoryLoadResult(entries.ToImmutable(), diagnostics.ToImmutable());
    }

    private static async ValueTask<ScriptFileMetadata?> ReadMetadataAsync(
        string sourcePath,
        ImmutableArray<ScriptDiagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var metadataPath = sourcePath + ".json";
        if (!File.Exists(metadataPath))
            return new ScriptFileMetadata();

        try
        {
            var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
            return JsonSerializer.Deserialize<ScriptFileMetadata>(json, JsonOptions)
                ?? throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new ScriptDiagnostic(
                "SCRIPT_METADATA_INVALID",
                "The script metadata could not be read.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Validation,
                metadataPath));
            return null;
        }
    }

    private static ScriptBuildResult Failure(string code, string message, string sourcePath) =>
        ScriptBuildResult.Failure(new ScriptDiagnostic(
            code,
            message,
            ScriptDiagnosticSeverity.Error,
            ScriptDiagnosticCategory.Validation,
            sourcePath));
}
