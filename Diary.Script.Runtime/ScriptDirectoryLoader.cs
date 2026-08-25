using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public sealed record ScriptFileMetadata(
    ScriptApiVersion ApiVersion = ScriptApiVersion.V1,
    string? Id = null,
    string? Name = null,
    string? Description = null,
    string? Engine = null,
    ScriptScope? Scope = null,
    IReadOnlyList<ScriptEditorTargetKind>? SupportedEditorTargets = null,
    ScriptEntryKind? EntryKind = null,
    string? Schedule = null,
    bool RunOnStartup = false,
    IReadOnlyList<ScriptAutomationTriggerKind>? Triggers = null,
    IReadOnlyDictionary<string, string>? DefaultArguments = null,
    IReadOnlyList<ScriptParameterDefinition>? Parameters = null,
    int? TimeoutSeconds = null);

public sealed record ScriptPackageManifest(
    string Entry,
    ScriptApiVersion ApiVersion = ScriptApiVersion.V1,
    string? Id = null,
    string? Name = null,
    string? Description = null,
    string? Engine = null,
    ScriptScope? Scope = null,
    IReadOnlyList<ScriptEditorTargetKind>? SupportedEditorTargets = null,
    ScriptEntryKind? EntryKind = null,
    string? Schedule = null,
    bool RunOnStartup = false,
    IReadOnlyList<ScriptAutomationTriggerKind>? Triggers = null,
    IReadOnlyDictionary<string, string>? DefaultArguments = null,
    IReadOnlyList<ScriptParameterDefinition>? Parameters = null,
    int? TimeoutSeconds = null);

public sealed record ScriptDirectoryEntry(
    string SourcePath,
    ScriptScope Scope,
    ScriptBuildResult? BuildResult = null,
    ScriptFileMetadata? Metadata = null);

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
        Converters = { new JsonStringEnumConverter() },
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
                        entries.Add(new ScriptDirectoryEntry(sourcePath, scope));
                        continue;
                    }

                    if (metadata.Engine is not null
                        && !string.Equals(selection.Engine.StableName, "csharp", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(metadata.Engine, selection.Engine.StableName, StringComparison.Ordinal))
                    {
                        var engineMismatch = Failure(
                            "SCRIPT_ENGINE_MISMATCH",
                            "The script metadata engine does not match the source extension.",
                            sourcePath);
                        entries.Add(new ScriptDirectoryEntry(sourcePath, scope, engineMismatch, metadata));
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
                                    metadata.Description,
                                    metadata.Engine ?? selection.Engine.StableName,
                                    metadata.SupportedEditorTargets,
                                    metadata.EntryKind,
                                    metadata.Parameters)),
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

                    if (result.Succeeded && result.Program is not null)
                    {
                        var runtimeConfigurationError = ValidateRuntimeConfiguration(
                            ScriptEntryKindResolver.Resolve(result.Program.Descriptor),
                            metadata,
                            sourcePath);
                        if (runtimeConfigurationError is not null)
                        {
                            DisposeProgram(result.Program);
                            result = runtimeConfigurationError;
                        }
                    }

                    if (result.Succeeded && result.Program is not null)
                    {
                        var entryKind = ScriptEntryKindResolver.Resolve(result.Program.Descriptor);
                        var binding = ScriptParameterBinder.Bind(
                            result.Program.Descriptor,
                            metadata.DefaultArguments,
                            null,
                            sourcePath,
                            requireRequired: entryKind == ScriptEntryKind.Automation);
                        if (!binding.Succeeded)
                        {
                            DisposeProgram(result.Program);
                            result = new ScriptBuildResult(false, null, result.Diagnostics.AddRange(binding.Diagnostics));
                        }
                    }

                    if (result.Succeeded && result.Program is not null
                        && !ScriptEntryKindResolver.IsCompatible(
                            ScriptEntryKindResolver.Resolve(result.Program.Descriptor), scope))
                    {
                        DisposeProgram(result.Program);
                        result = new ScriptBuildResult(
                            false,
                            null,
                            result.Diagnostics.Add(new ScriptDiagnostic(
                                "SCRIPT_ENTRY_KIND_MISMATCH",
                                "The script entry kind does not match its directory scope.",
                                ScriptDiagnosticSeverity.Error,
                                ScriptDiagnosticCategory.Validation,
                                sourcePath)));
                    }

                    if (result.Succeeded && result.Program is not null)
                    {
                        if (!MatchesMetadata(result.Program.Descriptor, metadata, result.EngineName))
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
                                result.EngineName,
                                metadata.DefaultArguments));
                        }
                    }

                    entries.Add(new ScriptDirectoryEntry(sourcePath, scope, result, metadata));
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
                        ApiVersion: manifest.ApiVersion,
                        Id: manifest.Id,
                        Name: manifest.Name,
                        Description: manifest.Description,
                        Engine: manifest.Engine,
                        Scope: manifest.Scope,
                        SupportedEditorTargets: manifest.SupportedEditorTargets,
                        EntryKind: manifest.EntryKind,
                        Schedule: manifest.Schedule,
                        RunOnStartup: manifest.RunOnStartup,
                        Triggers: manifest.Triggers,
                        DefaultArguments: manifest.DefaultArguments,
                        Parameters: manifest.Parameters,
                        TimeoutSeconds: manifest.TimeoutSeconds)));
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

    private static ScriptBuildResult? ValidateRuntimeConfiguration(
        ScriptEntryKind entryKind,
        ScriptFileMetadata metadata,
        string sourcePath)
    {
        var triggers = metadata.Triggers?.Distinct().ToArray() ?? [];
        var invalidTrigger = triggers.Any(trigger => !IsEventTrigger(trigger));
        if (entryKind == ScriptEntryKind.Automation)
        {
            if ((metadata.Schedule is not null
                    && !ScriptAutomationSchedule.TryParse(metadata.Schedule, out _))
                || invalidTrigger
                || (metadata.Schedule is null && !metadata.RunOnStartup && triggers.Length == 0))
            {
                return Failure(
                    "SCRIPT_SCHEDULE_INVALID",
                    "Automation scripts must declare a valid daily schedule, runOnStartup, or an event trigger.",
                    sourcePath);
            }
        }
        else if (metadata.Schedule is not null || metadata.RunOnStartup || triggers.Length > 0)
        {
            return Failure(
                "SCRIPT_SCHEDULE_INVALID",
                "Schedule, runOnStartup, and triggers are only allowed for automation scripts.",
                sourcePath);
        }

        return null;
    }

    private static bool IsEventTrigger(ScriptAutomationTriggerKind trigger) => trigger is
        ScriptAutomationTriggerKind.WorkItemCreated
        or ScriptAutomationTriggerKind.WorkItemSaved
        or ScriptAutomationTriggerKind.TagAdded;

    private static ScriptBuildResult Failure(string code, string message, string sourcePath) =>
        ScriptBuildResult.Failure(new ScriptDiagnostic(
            code,
            message,
            ScriptDiagnosticSeverity.Error,
            ScriptDiagnosticCategory.Validation,
            sourcePath));

    private static bool MatchesMetadata(
        ScriptDescriptor descriptor,
        ScriptFileMetadata metadata,
        string? engineName)
    {
        if (string.Equals(engineName, "csharp", StringComparison.OrdinalIgnoreCase))
            return true;
        if (metadata.Id is not null && !string.Equals(metadata.Id, descriptor.Id, StringComparison.Ordinal))
            return false;
        if (metadata.Name is not null && !string.Equals(metadata.Name, descriptor.Name, StringComparison.Ordinal))
            return false;
        if (metadata.Description is not null && !string.Equals(metadata.Description, descriptor.Description, StringComparison.Ordinal))
            return false;
        if (metadata.Scope is not null && metadata.Scope.Value != descriptor.Scope)
            return false;
        if (metadata.EntryKind is { } entryKind
            && entryKind != ScriptEntryKindResolver.Resolve(descriptor))
            return false;
        if (descriptor.Scope == ScriptScope.Editor && metadata.SupportedEditorTargets is not null
            && !metadata.SupportedEditorTargets.Order().SequenceEqual(
                descriptor.SupportedEditorTargets?.Order()
                ?? Enumerable.Empty<ScriptEditorTargetKind>()))
            return false;
        if (metadata.Parameters is not null
            && !ParameterDefinitionsEqual(metadata.Parameters, descriptor.Parameters))
            return false;
        return true;
    }

    private static bool ParameterDefinitionsEqual(
        IReadOnlyList<ScriptParameterDefinition> left,
        IReadOnlyList<ScriptParameterDefinition>? right) =>
        JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right ?? [], JsonOptions);

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
