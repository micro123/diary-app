namespace Diary.ScriptBase;

public interface IScript
{
    ScriptUsage Usage { get; }
}

public interface IApplicationScript : IScript
{
    void Execute(IScriptApi apiSet);
}

public interface IEditorScript : IScript
{
    bool ApplyToDay { get; }
    bool ApplyToRange { get; }
    
    void ExecuteDay(string date, IScriptApi apiSet);
    void ExecuteRange(string startDate, string endDate, IScriptApi apiSet);
}
