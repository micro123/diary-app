using Diary.PluginBase;
using System.Text.RegularExpressions;

namespace Diary.RedMine;

public sealed record RedMineInstanceConfiguration(
    string InstanceId,
    string DisplayName,
    RedMineConfig Configuration,
    IRedMineDb Database);

public sealed class RedMineInstance(RedMineInstanceConfiguration configuration) : ITrackerInstance
{
    private static readonly Regex IconPattern = new("^(fa|mdi)-[a-z0-9-]+$", RegexOptions.CultureInvariant);
    public RedMineInstanceSettings Settings => (RedMineInstanceSettings)configuration.Configuration;
    public RedMineConfig Configuration => configuration.Configuration;
    public IRedMineDb Database => configuration.Database;
    public string PluginId => RedMinePluginConstants.PluginId;
    public string InstanceId => configuration.InstanceId;
    public string DisplayName => configuration.DisplayName;
    public string Icon => Settings.Icon is { } icon && IconPattern.IsMatch(icon)
        ? icon
        : RedMinePluginConstants.DefaultIcon;
    public bool IsConfigured => configuration.Configuration.Valid();

    public IDictionary<int, object?>? LoadBindingsByDate(string date)
    {
        var bindings = configuration.Database.GetWorkTimeEntriesByDate(date);
        return bindings.ToDictionary(item => item.Key, item => (object?)item.Value);
    }
}
