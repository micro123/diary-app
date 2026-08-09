using Diary.Utils;

namespace Diary.Jira.UI;

public interface IJiraConfigurationEditService
{
    JiraPluginConfigurationEditSession Open(JiraPluginConfig configuration);
}

[DiAutoRegister(singleton: true, serviceType: typeof(IJiraConfigurationEditService))]
public sealed class JiraConfigurationEditService : IJiraConfigurationEditService
{
    public JiraPluginConfigurationEditSession Open(JiraPluginConfig configuration) => new(configuration);
}

public sealed class JiraPluginConfigurationEditSession
{
    private readonly JiraPluginConfig _source;
    private JiraPluginConfig _baseline;
    public JiraPluginConfig WorkingCopy { get; private set; }

    internal JiraPluginConfigurationEditSession(JiraPluginConfig source)
    {
        _source = source;
        _baseline = JiraConfigurationCopy.Clone(source);
        WorkingCopy = JiraConfigurationCopy.Clone(source);
    }

    public void Commit()
    {
        JiraConfigurationCopy.MergeChanges(_source, _baseline, WorkingCopy);
        Reload();
    }

    public void Reload()
    {
        _baseline = JiraConfigurationCopy.Clone(_source);
        WorkingCopy = JiraConfigurationCopy.Clone(_source);
    }
}

internal static class JiraConfigurationCopy
{
    public static JiraPluginConfig Clone(JiraPluginConfig source)
        => new() { Instances = source.Instances.Select(Clone).ToList() };

    public static JiraInstanceSettings Clone(JiraInstanceSettings source)
        => new()
        {
            InstanceId = source.InstanceId,
            DisplayName = source.DisplayName,
            Icon = source.Icon,
            Enabled = source.Enabled,
            ServerUrl = source.ServerUrl,
            UserName = source.UserName,
            ApiToken = source.ApiToken,
            UseBearerToken = source.UseBearerToken,
        };

    public static void MergeChanges(JiraPluginConfig target, JiraPluginConfig baseline, JiraPluginConfig working)
    {
        foreach (var removed in baseline.Instances.Where(old => working.Instances.All(item => item.InstanceId != old.InstanceId)))
        {
            var targetInstance = target.Instances.FirstOrDefault(item => item.InstanceId == removed.InstanceId);
            if (targetInstance is not null) target.Instances.Remove(targetInstance);
        }
        foreach (var workingInstance in working.Instances)
        {
            var baselineInstance = baseline.Instances.FirstOrDefault(item => item.InstanceId == workingInstance.InstanceId);
            var targetInstance = target.Instances.FirstOrDefault(item => item.InstanceId == workingInstance.InstanceId);
            if (baselineInstance is null)
            {
                if (targetInstance is null) target.Instances.Add(Clone(workingInstance));
                continue;
            }
            if (targetInstance is null) continue;
            targetInstance.DisplayName = Merge(targetInstance.DisplayName, baselineInstance.DisplayName, workingInstance.DisplayName);
            targetInstance.Icon = Merge(targetInstance.Icon, baselineInstance.Icon, workingInstance.Icon);
            targetInstance.Enabled = Merge(targetInstance.Enabled, baselineInstance.Enabled, workingInstance.Enabled);
            targetInstance.ServerUrl = Merge(targetInstance.ServerUrl, baselineInstance.ServerUrl, workingInstance.ServerUrl);
            targetInstance.UserName = Merge(targetInstance.UserName, baselineInstance.UserName, workingInstance.UserName);
            targetInstance.ApiToken = Merge(targetInstance.ApiToken, baselineInstance.ApiToken, workingInstance.ApiToken);
            targetInstance.UseBearerToken = Merge(targetInstance.UseBearerToken, baselineInstance.UseBearerToken, workingInstance.UseBearerToken);
        }
    }

    private static T Merge<T>(T target, T baseline, T working)
        => EqualityComparer<T>.Default.Equals(baseline, working) ? target : working;
}
