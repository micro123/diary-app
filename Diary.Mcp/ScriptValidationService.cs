using System.Collections.Immutable;
using System.Text;
using Diary.ScriptBase;

namespace Diary.Mcp;

public sealed class ScriptValidationService(IEnumerable<IScriptValidatorV1> validators)
{
    public const int MaxSourceBytes = 256 * 1024;
    public const int MaxDiagnostics = 100;
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(10);
    private readonly IReadOnlyDictionary<string, IScriptValidatorV1> _validators = validators
        .ToDictionary(validator => validator.StableName, StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _concurrency = new(2, 2);

    public async ValueTask<McpScriptValidationResult> ValidateAsync(
        string? language,
        string? source,
        CancellationToken cancellationToken = default)
    {
        var canonicalLanguage = NormalizeLanguage(language);
        if (canonicalLanguage is null || !_validators.TryGetValue(canonicalLanguage, out var validator))
        {
            return Failure(
                canonicalLanguage ?? string.Empty,
                "SCRIPT_LANGUAGE_UNSUPPORTED",
                "language 必须是 csharp、lua 或 python。");
        }
        if (string.IsNullOrWhiteSpace(source))
            return Failure(canonicalLanguage, "SCRIPT_SOURCE_REQUIRED", "source 不能为空。");
        if (Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
        {
            return Failure(
                canonicalLanguage,
                "SCRIPT_SOURCE_TOO_LARGE",
                $"source 的 UTF-8 大小不能超过 {MaxSourceBytes} 字节。");
        }

        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ValidationTimeout);
            ScriptValidationResult validation;
            try
            {
                validation = await validator.ValidateAsync(
                    new ScriptValidationRequest(VirtualSourcePath(canonicalLanguage), source),
                    timeout.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failure(
                    canonicalLanguage,
                    "SCRIPT_VALIDATION_TIMEOUT",
                    $"脚本校验未在 {ValidationTimeout.TotalSeconds:0} 秒内完成。");
            }
            catch (Exception)
            {
                return Failure(
                    canonicalLanguage,
                    "SCRIPT_VALIDATION_FAILED",
                    "脚本校验器发生内部错误。");
            }

            var diagnostics = LimitDiagnostics(validation.Diagnostics)
                .Select(MapDiagnostic)
                .ToArray();
            return new McpScriptValidationResult(
                validation.Succeeded,
                canonicalLanguage,
                "compile-only",
                diagnostics);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private static string? NormalizeLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "csharp" or "c#" or "cs" => "csharp",
        "lua" => "lua",
        "python" or "py" => "python",
        _ => null,
    };

    private static string VirtualSourcePath(string language) => language switch
    {
        "csharp" => "ai-script.cs",
        "lua" => "ai-script.lua",
        "python" => "ai-script.py",
        _ => "ai-script.txt",
    };

    private static ImmutableArray<ScriptDiagnostic> LimitDiagnostics(ImmutableArray<ScriptDiagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty || diagnostics.Length <= MaxDiagnostics)
            return diagnostics.IsDefault ? ImmutableArray<ScriptDiagnostic>.Empty : diagnostics;
        return diagnostics
            .Take(MaxDiagnostics - 1)
            .Append(new ScriptDiagnostic(
                "SCRIPT_DIAGNOSTICS_TRUNCATED",
                $"诊断数量超过 {MaxDiagnostics}，其余结果已省略。",
                ScriptDiagnosticSeverity.Warning,
                ScriptDiagnosticCategory.Validation))
            .ToImmutableArray();
    }

    private static McpScriptDiagnostic MapDiagnostic(ScriptDiagnostic diagnostic) => new(
        diagnostic.Code,
        diagnostic.Message,
        diagnostic.Severity.ToString(),
        diagnostic.Category.ToString(),
        diagnostic.Line,
        diagnostic.Column);

    private static McpScriptValidationResult Failure(string language, string code, string message) => new(
        false,
        language,
        "compile-only",
        [new McpScriptDiagnostic(code, message, "Error", "Validation", null, null)]);
}

public sealed record McpScriptValidationResult(
    bool Succeeded,
    string Language,
    string ValidationMode,
    IReadOnlyList<McpScriptDiagnostic> Diagnostics);

public sealed record McpScriptDiagnostic(
    string Code,
    string Message,
    string Severity,
    string Category,
    int? Line,
    int? Column);
