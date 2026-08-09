using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Diary.ScriptBase;

namespace Diary.Script.Py;

public sealed record PythonRuntimeResolution(
    bool Succeeded,
    string? ExecutablePath,
    Version? Version,
    ImmutableArray<ScriptDiagnostic> Diagnostics)
{
    public bool Found => Succeeded && ExecutablePath is not null;
    public string? Path => ExecutablePath;

    public static PythonRuntimeResolution Failure(params ScriptDiagnostic[] diagnostics) =>
        new(false, null, null, [.. diagnostics]);
}

public sealed class PythonRuntimeResolver
{
    public const string EnvironmentVariableName = "DIARY_PYTHON_PATH";
    public static readonly Version MinimumVersion = new(3, 10);

    private readonly Func<string, string?> _environment;
    private readonly TimeSpan _probeTimeout;

    public PythonRuntimeResolver(
        Func<string, string?>? environment = null,
        TimeSpan? probeTimeout = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(5);
        if (_probeTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
    }

    public PythonRuntimeResolution Resolve(string? explicitPath = null) =>
        ResolveAsync(explicitPath).GetAwaiter().GetResult();

    public async ValueTask<PythonRuntimeResolution> ResolveAsync(
        string? explicitPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configured = explicitPath;
        var isExplicit = !string.IsNullOrWhiteSpace(explicitPath);
        if (!isExplicit)
            configured = _environment(EnvironmentVariableName);

        var candidates = isExplicit
            ? [explicitPath!]
            : !string.IsNullOrWhiteSpace(configured)
                ? SplitConfiguredPaths(configured!)
                : GetPlatformCandidates();
        if (candidates.Count == 0)
            return PythonRuntimeResolution.Failure(NotFoundDiagnostic());

        ScriptDiagnostic? firstDiagnostic = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pathResult = NormalizeCandidate(candidate);
            if (pathResult.Diagnostic is not null)
            {
                firstDiagnostic ??= pathResult.Diagnostic;
                continue;
            }

            if (!File.Exists(pathResult.Path!))
            {
                firstDiagnostic ??= new ScriptDiagnostic(
                    "PYTHON_RUNTIME_NOT_FOUND",
                    $"The configured Python executable does not exist: {pathResult.Path}.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Runtime);
                continue;
            }

            PythonProcessResult probe;
            try
            {
                probe = await PythonProcessRunner.RunAsync(
                    pathResult.Path!, ["--version"], null, _probeTimeout, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                firstDiagnostic ??= new ScriptDiagnostic(
                    "PYTHON_RUNTIME_PROBE_FAILED",
                    $"The Python executable did not respond before the probe timeout: {pathResult.Path}.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Runtime);
                continue;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                firstDiagnostic ??= new ScriptDiagnostic(
                    "PYTHON_RUNTIME_PROBE_FAILED",
                    $"The Python executable could not be started ({pathResult.Path}): {exception.Message}",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Runtime);
                continue;
            }

            var version = ParseVersion(probe.StandardOutput, probe.StandardError);
            if (probe.ExitCode != 0 || version is null)
            {
                firstDiagnostic ??= new ScriptDiagnostic(
                    "PYTHON_RUNTIME_PROBE_FAILED",
                    $"The Python executable did not return a recognizable version: {pathResult.Path}.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Runtime);
                continue;
            }

            if (version < MinimumVersion)
            {
                firstDiagnostic ??= new ScriptDiagnostic(
                    "PYTHON_VERSION_UNSUPPORTED",
                    $"Python {MinimumVersion} or newer is required; found {version} at {pathResult.Path}.",
                    ScriptDiagnosticSeverity.Error,
                    ScriptDiagnosticCategory.Runtime);
                continue;
            }

            return new PythonRuntimeResolution(true, pathResult.Path, version, ImmutableArray<ScriptDiagnostic>.Empty);
        }

        return PythonRuntimeResolution.Failure(firstDiagnostic ?? NotFoundDiagnostic());
    }

    private static (string? Path, ScriptDiagnostic? Diagnostic) NormalizeCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return (null, new ScriptDiagnostic(
                "PYTHON_RUNTIME_PATH_INVALID",
                "The Python executable path is empty.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Runtime));
        if (!Path.IsPathFullyQualified(candidate))
            return (null, new ScriptDiagnostic(
                "PYTHON_RUNTIME_PATH_NOT_ABSOLUTE",
                "The Python executable path must be absolute.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Runtime));
        try
        {
            return (Path.GetFullPath(candidate), null);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return (null, new ScriptDiagnostic(
                "PYTHON_RUNTIME_PATH_INVALID",
                $"The Python executable path is invalid: {exception.Message}",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Runtime));
        }
    }

    private static IReadOnlyList<string> SplitConfiguredPaths(string configured) =>
        configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<string> GetPlatformCandidates()
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "python3.exe", "python.exe", "py.exe" }
            : new[] { "python3", "python3.14", "python3.13", "python3.12", "python3.11", "python3.10" };
        var directories = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            directories.Add(AppContext.BaseDirectory);
            directories.Add(Path.Combine(AppContext.BaseDirectory, "python"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            directories.Add("/usr/bin");
            directories.Add("/usr/local/bin");
            directories.Add("/opt/homebrew/bin");
        }
        else
        {
            directories.Add("/usr/bin");
            directories.Add("/usr/local/bin");
            directories.Add("/bin");
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
            directories.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return directories
            .SelectMany(directory => names.Select(name =>
                Path.IsPathFullyQualified(directory)
                    ? Path.Combine(directory, name)
                    : Path.Combine(Path.GetFullPath(directory), name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Version? ParseVersion(string standardOutput, string standardError)
    {
        var match = Regex.Match(
            standardOutput + Environment.NewLine + standardError,
            @"Python\s+(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?",
            RegexOptions.CultureInvariant);
        return !match.Success
            ? null
            : new Version(
                int.Parse(match.Groups["major"].Value),
                int.Parse(match.Groups["minor"].Value),
                match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0);
    }

    private static ScriptDiagnostic NotFoundDiagnostic() => new(
        "PYTHON_RUNTIME_NOT_FOUND",
        $"No usable Python {MinimumVersion} or newer executable was found for {GetPlatformName()}.",
        ScriptDiagnosticSeverity.Error,
        ScriptDiagnosticCategory.Runtime);

    private static string GetPlatformName() => OperatingSystem.IsWindows()
        ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "the current platform";
}
