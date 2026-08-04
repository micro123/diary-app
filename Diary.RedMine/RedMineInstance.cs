using Diary.RedMine.Models;
using Diary.PluginBase;

namespace Diary.RedMine;

public sealed record RedMineInstanceConfiguration(
    string InstanceId,
    string DisplayName,
    RedMineConfig Configuration,
    IRedMineDb Database);

public sealed class RedMineInstance(RedMineInstanceConfiguration configuration) : ITrackerInstance
{
    public string PluginId => RedMinePluginConstants.PluginId;
    public string InstanceId => configuration.InstanceId;
    public string DisplayName => configuration.DisplayName;
    public string Icon => "fa-cloud";
    public bool IsConfigured => configuration.Configuration.Valid();

    public IDictionary<int, object?>? LoadBindingsByDate(string date)
    {
        var bindings = configuration.Database.GetWorkTimeEntriesByDate(date);
        return bindings.ToDictionary(item => item.Key, item => (object?)item.Value);
    }
}
