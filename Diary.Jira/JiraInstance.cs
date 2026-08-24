using Diary.PluginBase;
using System.Text.RegularExpressions;

namespace Diary.Jira;

public sealed record JiraInstanceConfiguration(
    string InstanceId,
    string DisplayName,
    JiraInstanceSettings Configuration,
    IJiraDb Database);

public sealed class JiraInstance(JiraInstanceConfiguration configuration) : ITrackerInstance
{
    private static readonly Regex IconPattern = new("^(fa|mdi)-[a-z0-9-]+$", RegexOptions.CultureInvariant);
    public JiraInstanceSettings Settings => configuration.Configuration;
    public IJiraDb Database => configuration.Database;
    public string PluginId => JiraPluginConstants.PluginId;
    public string InstanceId => configuration.InstanceId;
    public string DisplayName => configuration.DisplayName;
    public string Icon => IconPattern.IsMatch(Settings.Icon) ? Settings.Icon : JiraPluginConstants.DefaultIcon;
    public bool IsConfigured => Settings.Valid();

    public IDictionary<int, object?>? LoadBindingsByDate(string date)
        => Database.GetWorkTimeEntriesByDate(date).ToDictionary(item => item.Key, item => (object?)item.Value);

    public IDictionary<int, object?>? LoadBindingsByDate(
        string date,
        IReadOnlyCollection<int> workItemIds)
        => Database.GetWorkTimeEntriesByWorkItemIds(workItemIds)
            .ToDictionary(item => item.Key, item => (object?)item.Value);
}
