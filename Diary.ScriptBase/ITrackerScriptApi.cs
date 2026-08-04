namespace Diary.ScriptBase;

public interface ITrackerScriptApi
{
    string PluginId { get; }

    object? Get(string key);
}
