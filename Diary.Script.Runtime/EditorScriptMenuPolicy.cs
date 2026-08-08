using System.Collections.Immutable;
using Diary.ScriptBase;

namespace Diary.Script.Runtime;

public static class EditorScriptMenuPolicy
{
    public static IReadOnlyList<IScriptProgramV1> GetRunnableScripts(IScriptCatalog catalog) =>
        catalog.GetAll()
            .Where(program => program.Descriptor.Scope == ScriptScope.Editor)
            .OrderBy(program => program.Descriptor.Name, StringComparer.Ordinal)
            .ToArray();

    public static ScriptExecutionRequest CreateRequest(
        ScriptEditorTarget target,
        IReadOnlyDictionary<string, string>? arguments = null) =>
        new(
            target,
            arguments is null
                ? null
                : arguments.ToImmutableDictionary(StringComparer.Ordinal),
            ScriptExecutionSource.Editor);

    public static string GetRangeLabel(ScriptEditorTargetKind kind) => kind switch
    {
        ScriptEditorTargetKind.Day => "当天",
        ScriptEditorTargetKind.Month => "当前月份",
        ScriptEditorTargetKind.Quarter => "当前季度",
        ScriptEditorTargetKind.Year => "当前年份",
        ScriptEditorTargetKind.WorkItem => "当前事项",
        _ => "当前目标",
    };
}
