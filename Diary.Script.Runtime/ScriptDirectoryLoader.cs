using System.Collections.Immutable;
using System.Text.Json;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public sealed record ScriptFileMetadata(
    bool Enabled = true,
    ScriptApiVersion ApiVersion = ScriptApiVersion.V1,
    string? Id = null,
    string? Name = null,
    string? Description = null,
    ScriptCapability? Capabilities = null);

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

    ValueTask SetEnabledAsync(string sourcePath, bool enabled, CancellationToken cancellationToken = default);
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
    private readonly HashSet<string> _registeredIds = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    public async ValueTask<ScriptDirectoryLoadResult> LoadAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var entries = ImmutableArray.CreateBuilder<ScriptDirectoryEntry>();
            var diagnostics = ImmutableArray.CreateBuilder<ScriptDiagnostic>();
            var loadedIds = new HashSet<string>(StringComparer.Ordinal);

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
                        DisposeProgram(result.Program);
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
                        if (!MatchesMetadata(result.Program.Descriptor, metadata))
                        {
                            DisposeProgram(result.Program);
                            result = new ScriptBuildResult(false, null, result.Diagnostics.Add(new ScriptDiagnostic(
                                "SCRIPT_METADATA_MISMATCH",
                                "The script metadata does not match the built descriptor.",
                                ScriptDiagnosticSeverity.Error,
                                ScriptDiagnosticCategory.Validation,
                                sourcePath)));
                        }
                    }

                    if (result.Succeeded && result.Program is not null)
                    {
                        var registration = loadedIds.Add(result.Program.Descriptor.Id)
                            ? catalog.RegisterOrReplace(result.Program)
                            : ScriptRegistrationResult.Failure(new ScriptDiagnostic(
                                "SCRIPT_ID_DUPLICATE",
                                $"A script with ID '{result.Program.Descriptor.Id}' is already registered.",
                                ScriptDiagnosticSeverity.Error,
                                ScriptDiagnosticCategory.Validation,
                                sourcePath));
                        if (!registration.Succeeded)
                        {
                            DisposeProgram(result.Program);
                            result = new ScriptBuildResult(false, null, result.Diagnostics.AddRange(registration.Diagnostics));
                        }
                        else
                            _registeredIds.Add(result.Program.Descriptor.Id);
                    }

                    entries.Add(new ScriptDirectoryEntry(sourcePath, scope, true, result));
                    diagnostics.AddRange(result.Diagnostics);
                }
            }

            var currentIds = entries
                .Where(entry => entry.BuildResult?.Succeeded == true)
                .Select(entry => entry.BuildResult!.Program!.Descriptor.Id)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var staleId in _registeredIds.Except(currentIds, StringComparer.Ordinal).ToArray())
            {
                catalog.Remove(staleId);
                _registeredIds.Remove(staleId);
            }

            return new ScriptDirectoryLoadResult(entries.ToImmutable(), diagnostics.ToImmutable());
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async ValueTask SetEnabledAsync(
        string sourcePath,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            var metadataPath = sourcePath + ".json";
            ScriptFileMetadata metadata;
            if (File.Exists(metadataPath))
            {
                var json = await File.ReadAllTextAsync(metadataPath, cancellationToken);
                metadata = JsonSerializer.Deserialize<ScriptFileMetadata>(json, JsonOptions)
                    ?? new ScriptFileMetadata();
            }
            else
            {
                metadata = new ScriptFileMetadata();
            }

            var updated = metadata with { Enabled = enabled };
            var temporaryPath = metadataPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    JsonSerializer.Serialize(updated, JsonOptions),
                    cancellationToken);
                File.Move(temporaryPath, metadataPath, true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            _loadGate.Release();
        }
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

    private static bool MatchesMetadata(
        ScriptDescriptor descriptor,
        ScriptFileMetadata metadata)
    {
        if (metadata.Id is not null && !string.Equals(metadata.Id, descriptor.Id, StringComparison.Ordinal))
            return false;
        if (metadata.Name is not null && !string.Equals(metadata.Name, descriptor.Name, StringComparison.Ordinal))
            return false;
        if (metadata.Description is not null && !string.Equals(metadata.Description, descriptor.Description, StringComparison.Ordinal))
            return false;
        if (metadata.Capabilities is not null && metadata.Capabilities.Value != descriptor.Capabilities)
            return false;
        return true;
    }

    private static void DisposeProgram(IScriptProgramV1 program)
    {
        if (program is IDisposable disposable)
            disposable.Dispose();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
