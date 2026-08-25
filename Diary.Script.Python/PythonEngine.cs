using System.Collections.Immutable;
using System.Text.Json;
using Diary.ScriptBase;

namespace Diary.Script.Py;

public sealed class PythonEngine : IScriptEngineV1, IScriptValidatorV1
{
    private readonly PythonRuntimeResolver _runtimeResolver;
    private readonly ScriptDescriptorHint? _defaultDescriptorHint;
    private readonly TimeSpan _syntaxCheckTimeout;

    public PythonEngine(
        PythonRuntimeResolver? runtimeResolver = null,
        ScriptDescriptorHint? descriptorHint = null,
        TimeSpan? syntaxCheckTimeout = null)
    {
        _runtimeResolver = runtimeResolver ?? new PythonRuntimeResolver();
        _defaultDescriptorHint = descriptorHint;
        _syntaxCheckTimeout = syntaxCheckTimeout ?? TimeSpan.FromSeconds(10);
        if (_syntaxCheckTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(syntaxCheckTimeout));
    }

    public string Name => "python";
    public string StableName => Name;
    public string Version => typeof(PythonEngine).Assembly.GetName().Version?.ToString() ?? "1.0";

    public ScriptMatchResult Match(ScriptMatchRequest request) =>
        new(request.SourcePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase));

    public ValueTask<ScriptBuildResult> BuildAsync(
        ScriptBuildRequest request,
        CancellationToken cancellationToken = default) =>
        BuildCoreAsync(request, request.DescriptorHint ?? _defaultDescriptorHint, cancellationToken);

    private async ValueTask<ScriptBuildResult> BuildCoreAsync(
        ScriptBuildRequest request,
        ScriptDescriptorHint? descriptorHint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var hintDiagnostic = ValidateDescriptorHint(descriptorHint, request.SourcePath);
        if (hintDiagnostic is not null)
            return ScriptBuildResult.Failure(hintDiagnostic);

        var (validation, runtime) = await ValidateCoreAsync(
            new ScriptValidationRequest(request.SourcePath, request.Source, request.ApiVersion),
            cancellationToken);
        if (!validation.Succeeded || runtime is null)
            return new ScriptBuildResult(false, null, validation.Diagnostics);

        var descriptor = new ScriptDescriptor(
            descriptorHint!.Id!,
            descriptorHint.Name!,
            request.ApiVersion,
            descriptorHint.Scope!.Value,
            descriptorHint.Description,
            descriptorHint.SupportedEditorTargets,
            descriptorHint.EntryKind,
            descriptorHint.Parameters);
        return ScriptBuildResult.Success(new PythonProgram(
            descriptor,
            request.SourcePath,
            request.Source,
            runtime));
    }

    public async ValueTask<ScriptValidationResult> ValidateAsync(
        ScriptValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var (validation, _) = await ValidateCoreAsync(request, cancellationToken);
        return validation;
    }

    private async ValueTask<(ScriptValidationResult Validation, PythonRuntimeResolution? Runtime)> ValidateCoreAsync(
        ScriptValidationRequest request,
        CancellationToken cancellationToken)
    {
        PythonRuntimeResolution runtime;
        try
        {
            runtime = await _runtimeResolver.ResolveAsync(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (Failure(new ScriptDiagnostic(
                "PYTHON_RUNTIME_PROBE_FAILED",
                $"The Python runtime probe failed: {exception.Message}",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Runtime,
                request.SourcePath)), null);
        }

        if (!runtime.Succeeded || runtime.ExecutablePath is null)
        {
            var diagnostics = runtime.Diagnostics
                .Select(diagnostic => diagnostic with { SourcePath = request.SourcePath })
                .ToImmutableArray();
            return (new ScriptValidationResult(false, diagnostics) { EngineName = StableName }, null);
        }

        var syntaxDiagnostics = await CheckSyntaxAsync(runtime.ExecutablePath, request, cancellationToken);
        if (!syntaxDiagnostics.IsEmpty)
            return (new ScriptValidationResult(false, syntaxDiagnostics) { EngineName = StableName }, runtime);
        return (ScriptValidationResult.Success() with { EngineName = StableName }, runtime);
    }

    private ScriptValidationResult Failure(ScriptDiagnostic diagnostic) =>
        ScriptValidationResult.Failure(diagnostic) with { EngineName = StableName };

    private static ScriptDiagnostic? ValidateDescriptorHint(
        ScriptDescriptorHint? hint,
        string sourcePath)
    {
        if (hint is null
            || string.IsNullOrWhiteSpace(hint.Id)
            || string.IsNullOrWhiteSpace(hint.Name)
            || hint.Scope is null
            || hint.Scope is null)
        {
            return new ScriptDiagnostic(
                "PYTHON_DESCRIPTOR_HINT_REQUIRED",
                "Python scripts require metadata hints for Id, Name, and Scope.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Validation,
                sourcePath);
        }
        if (!Enum.IsDefined(hint.Scope.Value)
            )
        {
            return new ScriptDiagnostic(
                "PYTHON_DESCRIPTOR_HINT_INVALID",
                "The Python descriptor metadata hint contains an unsupported value.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Validation,
                sourcePath);
        }
        if (hint.EngineName is not null
            && !string.Equals(hint.EngineName, "python", StringComparison.Ordinal))
        {
            return new ScriptDiagnostic(
                "PYTHON_ENGINE_MISMATCH",
                "The Python descriptor metadata hint names a different engine.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Validation,
                sourcePath);
        }
        return null;
    }

    private async ValueTask<ImmutableArray<ScriptDiagnostic>> CheckSyntaxAsync(
        string executablePath,
        ScriptValidationRequest request,
        CancellationToken cancellationToken)
    {
        PythonProcessResult process;
        try
        {
            process = await PythonProcessRunner.RunAsync(
                executablePath,
                ["-X", "utf8", "-I", "-c", PythonSyntaxProbe, request.SourcePath],
                request.Source,
                _syntaxCheckTimeout,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return [new ScriptDiagnostic(
                "PYTHON_SYNTAX_CHECK_FAILED",
                $"The Python syntax check could not be started: {exception.Message}",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Engine,
                request.SourcePath)];
        }
        catch (OperationCanceledException)
        {
            return [new ScriptDiagnostic(
                "PYTHON_SYNTAX_CHECK_FAILED",
                "The Python syntax check did not finish before the timeout.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Engine,
                request.SourcePath)];
        }

        var diagnostics = ParseProbeDiagnostics(process.StandardOutput, request.SourcePath);
        if (!diagnostics.IsEmpty)
            return diagnostics;
        if (process.ExitCode != 0)
        {
            var detail = SummarizeProcessOutput(process.StandardError, process.StandardOutput);
            return [new ScriptDiagnostic(
                "PYTHON_SYNTAX_CHECK_FAILED",
                $"The Python syntax check returned an invalid result (exit code {process.ExitCode}){detail}.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Syntax,
                request.SourcePath)];
        }
        return ImmutableArray<ScriptDiagnostic>.Empty;
    }

    private static string SummarizeProcessOutput(string standardError, string standardOutput)
    {
        var detail = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
        detail = detail.Trim().ReplaceLineEndings(" ");
        if (detail.Length > 512)
            detail = detail[..512] + "…";
        return detail.Length == 0 ? string.Empty : $": {detail}";
    }

    private static ImmutableArray<ScriptDiagnostic> ParseProbeDiagnostics(
        string output,
        string sourcePath)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("diagnostics", out var items)
                || items.ValueKind != JsonValueKind.Array)
                return ImmutableArray<ScriptDiagnostic>.Empty;

            return items.EnumerateArray().Select(item => new ScriptDiagnostic(
                    item.GetProperty("code").GetString() ?? "PYTHON_SYNTAX_ERROR",
                    item.GetProperty("message").GetString() ?? "The Python source is invalid.",
                    ScriptDiagnosticSeverity.Error,
                    item.GetProperty("category").GetString() == "Security"
                        ? ScriptDiagnosticCategory.Security
                        : ScriptDiagnosticCategory.Syntax,
                    sourcePath,
                    item.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number
                        ? line.GetInt32()
                        : null,
                    item.TryGetProperty("column", out var column) && column.ValueKind == JsonValueKind.Number
                        ? column.GetInt32()
                        : null))
                .ToImmutableArray();
        }
        catch (JsonException)
        {
            return ImmutableArray<ScriptDiagnostic>.Empty;
        }
    }

    private const string PythonSyntaxProbe = """
import ast
import json
import sys

source = sys.stdin.read()
diagnostics = []
try:
    tree = ast.parse(source, filename=sys.argv[1])
except SyntaxError as error:
    diagnostics.append({"code": "PYTHON_SYNTAX_ERROR", "category": "Syntax", "message": error.msg, "line": error.lineno, "column": error.offset})
else:
    forbidden_names = {"__builtins__", "__import__", "__loader__", "__spec__", "breakpoint", "compile", "delattr", "eval", "exec", "getattr", "globals", "help", "input", "locals", "memoryview", "open", "quit", "setattr", "vars"}
    for node in ast.walk(tree):
        if isinstance(node, (ast.Import, ast.ImportFrom)):
            diagnostics.append({"code": "PYTHON_API_FORBIDDEN", "category": "Security", "message": "Python scripts cannot import modules.", "line": node.lineno, "column": node.col_offset + 1})
        elif isinstance(node, ast.Name) and node.id in forbidden_names:
            diagnostics.append({"code": "PYTHON_API_FORBIDDEN", "category": "Security", "message": "The Python script uses a forbidden runtime API.", "line": node.lineno, "column": node.col_offset + 1})
        elif isinstance(node, ast.Attribute) and node.attr.startswith("__"):
            diagnostics.append({"code": "PYTHON_API_FORBIDDEN", "category": "Security", "message": "The Python script uses a forbidden runtime attribute.", "line": node.lineno, "column": node.col_offset + 1})
    unique = []
    seen = set()
    for diagnostic in diagnostics:
        key = (diagnostic["code"], diagnostic["line"], diagnostic["column"])
        if key not in seen:
            seen.add(key)
            unique.append(diagnostic)
    diagnostics = unique
print(json.dumps({"diagnostics": diagnostics}, ensure_ascii=True))
sys.exit(1 if diagnostics else 0)
""";
}
