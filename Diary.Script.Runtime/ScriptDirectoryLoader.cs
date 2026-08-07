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
    ScriptCapability? Capabilities = null,
    string? Engine = null,
    ScriptScope? Scope = null);

public sealed record ScriptPackageManifest(
    string Entry,
    bool Enabled = true,
    ScriptApiVersion ApiVersion = ScriptApiVersion.V1,
    string? Id = null,
    string? Name = null,
    string? Description = null,
    ScriptCapability? Capabilities = null,
    string? Engine = null,
    ScriptScope? Scope = null);

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
                foreach (var candidate in await DiscoverCandidatesAsync(directory, diagnostics, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourcePath = candidate.SourcePath;
                    var selection = engines.Select(new ScriptMatchRequest(sourcePath));
                    if (sourcePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        || selection.Engine is null)
                    {
                        continue;
                    }

                    var metadata = candidate.Metadata
                        ?? await ReadMetadataAsync(sourcePath, diagnostics, cancellationToken);
                    if (metadata is null)
                    {
                        entries.Add(new ScriptDirectoryEntry(sourcePath, scope, false));
                        continue;
                    }

                    if (metadata.Engine is not null
                        && !string.Equals(metadata.Engine, selection.Engine.StableName, StringComparison.Ordinal))
                    {
                        var engineMismatch = Failure(
                            "SCRIPT_ENGINE_MISMATCH",
                            "The script metadata engine does not match the source extension.",
                            sourcePath);
                        entries.Add(new ScriptDirectoryEntry(sourcePath, scope, false, engineMismatch));
                        diagnostics.AddRange(engineMismatch.Diagnostics);
                        continue;
                    }

                    ScriptBuildResult result;
                    try
                    {
                        var source = await File.ReadAllTextAsync(sourcePath, cancellationToken);
                        result = await buildService.BuildAsync(
                            new ScriptBuildRequest(
                                sourcePath,
                                source,
                                metadata.ApiVersion,
                                new ScriptDescriptorHint(
                                    metadata.Id,
                                    metadata.Name,
                                    metadata.Scope ?? scope,
                                    metadata.Capabilities,
                                    metadata.Description,
                                    metadata.Engine ?? selection.Engine.StableName)),
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
                        {
                            _registeredIds.Add(result.Program.Descriptor.Id);
                            catalog.SetSource(result.Program.Descriptor.Id, new ScriptSourceInfo(
                                sourcePath,
                                await File.ReadAllTextAsync(sourcePath, cancellationToken),
                                result.EngineName));
                        }
                    }

                    var loaded = result.Succeeded && result.Program is not null;
                    entries.Add(new ScriptDirectoryEntry(sourcePath, scope, loaded, result));
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

    private static async ValueTask<IReadOnlyList<ScriptSourceCandidate>> DiscoverCandidatesAsync(
        string directory,
        ImmutableArray<ScriptDiagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var candidates = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(path => new ScriptSourceCandidate(path, null))
            .ToList();
        foreach (var packageDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(packageDirectory, "manifest.json");
            if (!File.Exists(manifestPath))
                continue;

            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                var manifest = JsonSerializer.Deserialize<ScriptPackageManifest>(json, JsonOptions)
                    ?? throw new JsonException();
                var packageRoot = Path.GetFullPath(packageDirectory);
                if (string.IsNullOrWhiteSpace(manifest.Entry))
                    throw new InvalidDataException("The package entry is required.");
                var entryPath = Path.GetFullPath(Path.Combine(packageRoot, manifest.Entry));
                if (!entryPath.StartsWith(packageRoot + Path.DirectorySeparatorChar,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                    || !File.Exists(entryPath)
                    || File.GetAttributes(entryPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException("The package entry must stay inside the package directory.");
                }

                candidates.Add(new ScriptSourceCandidate(
                    entryPath,
                    new ScriptFileMetadata(
                        manifest.Enabled,
                        manifest.ApiVersion,
                        manifest.Id,
                        manifest.Name,
                        manifest.Description,
                        manifest.Capabilities,
                        manifest.Engine,
                        manifest.Scope)));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                diagnostics.Add(new ScriptDiagnostic(
                    "SCRIPT_PACKAGE_INVALID",
                    "The script package manifest or entry path is invalid.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Validation,
                    manifestPath));
            }
        }

        return candidates.OrderBy(candidate => candidate.SourcePath, StringComparer.Ordinal).ToArray();
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
        if (metadata.Scope is not null && metadata.Scope.Value != descriptor.Scope)
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

    private sealed record ScriptSourceCandidate(string SourcePath, ScriptFileMetadata? Metadata);
}
