using Diary.Utils;

namespace Diary.RedMine.UI;

public interface IRedMineConfigurationEditService
{
    RedMinePluginConfigurationEditSession Open(RedMinePluginConfig configuration);
    RedMineInstanceConfigurationEditSession Open(RedMineInstanceSettings instance);
}

[DiAutoRegister(singleton: true, serviceType: typeof(IRedMineConfigurationEditService))]
public sealed class RedMineConfigurationEditService : IRedMineConfigurationEditService
{
    public RedMinePluginConfigurationEditSession Open(RedMinePluginConfig configuration)
        => new(configuration);

    public RedMineInstanceConfigurationEditSession Open(RedMineInstanceSettings instance)
        => new(instance);
}

public sealed class RedMinePluginConfigurationEditSession
{
    private readonly RedMinePluginConfig _source;
    private RedMinePluginConfig _baseline;

    public RedMinePluginConfig WorkingCopy { get; private set; }

    internal RedMinePluginConfigurationEditSession(RedMinePluginConfig source)
    {
        _source = source;
        _baseline = RedMineConfigurationCopy.Clone(source);
        WorkingCopy = RedMineConfigurationCopy.Clone(source);
    }

    public void Commit()
    {
        foreach (var removed in _baseline.Instances
                     .Where(old => WorkingCopy.Instances.All(current => current.InstanceId != old.InstanceId)))
        {
            var source = _source.Instances.FirstOrDefault(item => item.InstanceId == removed.InstanceId);
            if (source is not null)
                _source.Instances.Remove(source);
        }

        foreach (var working in WorkingCopy.Instances)
        {
            var baseline = _baseline.Instances.FirstOrDefault(item => item.InstanceId == working.InstanceId);
            var source = _source.Instances.FirstOrDefault(item => item.InstanceId == working.InstanceId);
            if (baseline is null)
            {
                if (source is null)
                    _source.Instances.Add(RedMineConfigurationCopy.Clone(working));
                continue;
            }
            if (source is not null)
                RedMineConfigurationCopy.MergeChanges(source, baseline, working);
        }
        Reload();
    }

    public void Reload()
    {
        _baseline = RedMineConfigurationCopy.Clone(_source);
        WorkingCopy = RedMineConfigurationCopy.Clone(_source);
    }
}

public sealed class RedMineInstanceConfigurationEditSession
{
    private readonly RedMineInstanceSettings _source;
    private RedMineInstanceSettings _baseline;

    public RedMineInstanceSettings WorkingCopy { get; private set; }

    internal RedMineInstanceConfigurationEditSession(RedMineInstanceSettings source)
    {
        _source = source;
        _baseline = RedMineConfigurationCopy.Clone(source);
        WorkingCopy = RedMineConfigurationCopy.Clone(source);
    }

    public void Commit()
    {
        RedMineConfigurationCopy.MergeChanges(_source, _baseline, WorkingCopy);
        Reload();
    }

    public void Reload()
    {
        _baseline = RedMineConfigurationCopy.Clone(_source);
        WorkingCopy = RedMineConfigurationCopy.Clone(_source);
    }
}

internal static class RedMineConfigurationCopy
{
    public static RedMinePluginConfig Clone(RedMinePluginConfig source)
        => new() { Instances = source.Instances.Select(Clone).ToList() };

    public static RedMineInstanceSettings Clone(RedMineInstanceSettings source)
        => new()
        {
            InstanceId = source.InstanceId,
            DisplayName = source.DisplayName,
            Enabled = source.Enabled,
            RedMineServerUrl = source.RedMineServerUrl,
            RedMineApiKey = source.RedMineApiKey,
            EnableProxy = source.EnableProxy,
            ProxyServer = source.ProxyServer,
            TagRules = source.TagRules.Select(Clone).ToList(),
        };

    public static void MergeChanges(
        RedMineInstanceSettings target,
        RedMineInstanceSettings baseline,
        RedMineInstanceSettings working)
    {
        target.DisplayName = Merge(target.DisplayName, baseline.DisplayName, working.DisplayName);
        target.Enabled = Merge(target.Enabled, baseline.Enabled, working.Enabled);
        target.RedMineServerUrl = Merge(target.RedMineServerUrl, baseline.RedMineServerUrl, working.RedMineServerUrl);
        target.RedMineApiKey = Merge(target.RedMineApiKey, baseline.RedMineApiKey, working.RedMineApiKey);
        target.EnableProxy = Merge(target.EnableProxy, baseline.EnableProxy, working.EnableProxy);
        target.ProxyServer = Merge(target.ProxyServer, baseline.ProxyServer, working.ProxyServer);
        MergeRuleChanges(target.TagRules, baseline.TagRules, working.TagRules);
    }

    private static RedMineTagRule Clone(RedMineTagRule source)
        => new()
        {
            RuleId = source.RuleId,
            TagId = source.TagId,
            ActivityId = source.ActivityId,
            IssueId = source.IssueId,
            Enabled = source.Enabled,
        };

    private static void MergeRuleChanges(
        IList<RedMineTagRule> target,
        IList<RedMineTagRule> baseline,
        IList<RedMineTagRule> working)
    {
        foreach (var removed in baseline.Where(old => working.All(rule => rule.RuleId != old.RuleId)))
        {
            var targetRule = target.FirstOrDefault(rule => rule.RuleId == removed.RuleId);
            if (targetRule is not null)
                target.Remove(targetRule);
        }

        foreach (var workingRule in working)
        {
            var baselineRule = baseline.FirstOrDefault(rule => rule.RuleId == workingRule.RuleId);
            var targetRule = target.FirstOrDefault(rule => rule.RuleId == workingRule.RuleId);
            if (baselineRule is null)
            {
                if (targetRule is null)
                    target.Add(Clone(workingRule));
                continue;
            }
            if (targetRule is null)
                continue;
            targetRule.TagId = Merge(targetRule.TagId, baselineRule.TagId, workingRule.TagId);
            targetRule.ActivityId = Merge(targetRule.ActivityId, baselineRule.ActivityId, workingRule.ActivityId);
            targetRule.IssueId = Merge(targetRule.IssueId, baselineRule.IssueId, workingRule.IssueId);
            targetRule.Enabled = Merge(targetRule.Enabled, baselineRule.Enabled, workingRule.Enabled);
        }
    }

    private static T Merge<T>(T target, T baseline, T working)
        => EqualityComparer<T>.Default.Equals(baseline, working) ? target : working;
}
