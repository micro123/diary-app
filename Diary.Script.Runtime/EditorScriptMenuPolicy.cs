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
        string startDate,
        string endDate,
        ScriptTimeGranularity granularity,
        int? workItemId = null) =>
        new(
            new ScriptTarget(
                ScriptScope.Editor,
                new EditorScriptContext(startDate, endDate, granularity),
                workItemId is > 0
                    ? new ScriptBusinessTarget(ScriptBusinessTargetKind.WorkItem, workItemId.Value.ToString())
                    : null),
            Source: ScriptExecutionSource.Editor);

    public static string GetRangeLabel(ScriptTimeGranularity granularity) => granularity switch
    {
        ScriptTimeGranularity.Day => "当天",
        ScriptTimeGranularity.Month => "当前月份",
        ScriptTimeGranularity.Year => "当前年份",
        _ => "所选范围",
    };
}
