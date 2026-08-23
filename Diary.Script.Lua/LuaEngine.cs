using System.Text;
using System.Text.RegularExpressions;
using Diary.ScriptBase;
using LuaState = NLua.Lua;

namespace Diary.Script.Lua;

public sealed class LuaEngine : IScriptEngineV1, IScriptValidatorV1
{
    public string Name => "lua";
    public string StableName => Name;
    public string Version => typeof(LuaEngine).Assembly.GetName().Version?.ToString() ?? "1.0";

    public ScriptMatchResult Match(ScriptMatchRequest request) =>
        new(request.SourcePath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));

    public ValueTask<ScriptBuildResult> BuildAsync(
        ScriptBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var hintDiagnostic = ValidateDescriptorHint(request.DescriptorHint, request.SourcePath);
        if (hintDiagnostic is not null)
            return ValueTask.FromResult(ScriptBuildResult.Failure(hintDiagnostic));

        var validation = ValidateSource(request.SourcePath, request.Source);
        if (!validation.Succeeded)
            return ValueTask.FromResult(new ScriptBuildResult(false, null, validation.Diagnostics));

        var hint = request.DescriptorHint!;
        var descriptor = new ScriptDescriptor(
            hint.Id!,
            hint.Name!,
            request.ApiVersion,
            hint.Scope!.Value,
            hint.Description,
            hint.SupportedEditorTargets,
            hint.EntryKind);
        return ValueTask.FromResult(ScriptBuildResult.Success(new LuaProgram(
            descriptor,
            request.SourcePath,
            request.Source)));
    }

    public ValueTask<ScriptValidationResult> ValidateAsync(
        ScriptValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var result = ValidateSource(request.SourcePath, request.Source);
        return ValueTask.FromResult(result with { EngineName = StableName });
    }

    private static ScriptValidationResult ValidateSource(string sourcePath, string source)
    {
        try
        {
            using var lua = LuaSandbox.Create();
            // NLua 的 string 重载按系统 ANSI 编码转换，中文会被替换成 ?；统一用 UTF-8 byte[] 重载。
            lua.LoadString(Encoding.UTF8.GetBytes(source), sourcePath);
            return ScriptValidationResult.Success();
        }
        catch (Exception exception)
        {
            var runtimeDiagnostic = LuaRuntimeDiagnostics.Create(exception, sourcePath);
            return ScriptValidationResult.Failure(runtimeDiagnostic ?? new ScriptDiagnostic(
                "LUA_SYNTAX_ERROR",
                exception.Message,
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Syntax,
                sourcePath,
                ParseLine(exception.Message),
                ParseColumn(exception.Message)));
        }
    }

    private static ScriptDiagnostic? ValidateDescriptorHint(
        ScriptDescriptorHint? hint,
        string sourcePath)
    {
        if (hint is null
            || string.IsNullOrWhiteSpace(hint.Id)
            || string.IsNullOrWhiteSpace(hint.Name)
            || hint.Scope is null)
        {
            return new ScriptDiagnostic(
                "LUA_DESCRIPTOR_HINT_REQUIRED",
                "Lua scripts require metadata hints for Id, Name, and Scope.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Validation,
                sourcePath);
        }
        if (!Enum.IsDefined(hint.Scope.Value))
        {
            return new ScriptDiagnostic(
                "LUA_DESCRIPTOR_HINT_INVALID",
                "The Lua descriptor metadata hint contains an unsupported value.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Validation,
                sourcePath);
        }
        if (hint.EngineName is not null
            && !string.Equals(hint.EngineName, "lua", StringComparison.Ordinal))
        {
            return new ScriptDiagnostic(
                "LUA_ENGINE_MISMATCH",
                "The Lua descriptor metadata hint names a different engine.",
                ScriptDiagnosticSeverity.Error,
                ScriptDiagnosticCategory.Validation,
                sourcePath);
        }
        return null;
    }

    private static int? ParseLine(string message) => ParseLocation(message).Line;

    private static int? ParseColumn(string message) => ParseLocation(message).Column;

    private static (int? Line, int? Column) ParseLocation(string message)
    {
        var match = Regex.Match(message, @":(?<line>\d+)(?::(?<column>\d+))?(?:[:\s]|$)", RegexOptions.CultureInvariant);
        return match.Success
            ? (int.Parse(match.Groups["line"].Value), match.Groups["column"].Success
                ? int.Parse(match.Groups["column"].Value)
                : null)
            : (null, null);
    }
}

internal static class LuaRuntimeDiagnostics
{
    public static ScriptDiagnostic? Create(Exception exception, string sourcePath)
    {
        if (!IsNativeFailure(exception))
            return null;
        return new ScriptDiagnostic(
            "LUA_RUNTIME_UNAVAILABLE",
            $"Lua native runtime could not be loaded for '{sourcePath}': {exception.Message}",
            ScriptDiagnosticSeverity.Error,
            ScriptDiagnosticCategory.Engine,
            sourcePath);
    }

    private static bool IsNativeFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DllNotFoundException or BadImageFormatException)
                return true;
        }
        return exception is TypeInitializationException
            && exception.InnerException is not null
            && IsNativeFailure(exception.InnerException);
    }
}

internal static class LuaSandbox
{
    private const string RestrictedLibraries = "io = nil; os = nil; debug = nil; package = nil; require = nil; dofile = nil; loadfile = nil; load = nil; loadstring = nil; import = nil; luanet = nil; clr = nil";

    public static LuaState Create()
    {
        var lua = new LuaState();
        // KeraLua 默认使用 ASCII 做字符串双向转换，中文会被替换成 ?；统一改为 UTF-8。
        lua.State.Encoding = Encoding.UTF8;
        lua.DoString(Encoding.UTF8.GetBytes(RestrictedLibraries));
        return lua;
    }
}
