using Diary.ScriptBase;
using NLua;

namespace Diary.Script.Lua;

public sealed class LuaProgram(
    ScriptDescriptor descriptor,
    string sourcePath,
    string source) : IScriptProgramV1
{
    public ScriptDescriptor Descriptor { get; } = descriptor;
    public string SourcePath { get; } = sourcePath;
    public string Source { get; } = source;

    public ValueTask<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        IScriptExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(ScriptExecutionResult.Cancelled());

        try
        {
            using var lua = LuaSandbox.Create();
            lua.LoadString(Source, SourcePath);
            lua.DoString(Source);
            var entry = lua.GetFunction("main") ?? lua.GetFunction("execute");
            if (entry is null)
            {
                return ValueTask.FromResult(Failure(
                    "LUA_ENTRYPOINT_MISSING",
                    "Lua scripts must define main(context) or execute(context).",
                    ScriptDiagnosticCategory.Validation));
            }

            entry.Call();
            return ValueTask.FromResult(ScriptExecutionResult.Succeeded());
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Failure(
                "LUA_EXECUTION_FAILED",
                exception.Message,
                ScriptDiagnosticCategory.Runtime));
        }
    }

    private static ScriptExecutionResult Failure(
        string code,
        string message,
        ScriptDiagnosticCategory category) =>
        new(ScriptExecutionStatus.Failed, [new ScriptDiagnostic(
            code,
            message,
            ScriptDiagnosticSeverity.Error,
            category)]);
}
