using Diary.PluginBase;
using Diary.PluginUI;

namespace Diary.App;

public sealed class TrackerTemplateContributorRegistry
{
    private readonly List<ITrackerTemplateContributor> _contributors = new();

    public IReadOnlyList<ITrackerTemplateContributor> Contributors => _contributors;

    public void Register(
        IEnumerable<ITrackerTemplateContributorFactory> factories,
        IEnumerable<ITrackerInstance> instances)
    {
        _contributors.Clear();
        foreach (var instance in instances)
        {
            var factory = factories.FirstOrDefault(x => x.PluginId == instance.PluginId);
            if (factory is null)
                continue;
            var contributor = factory.Create(instance);
            if (contributor.PluginId != instance.PluginId
                || contributor.InstanceId != instance.InstanceId)
                continue;
            if (_contributors.Any(x => x.PluginId == contributor.PluginId
                && x.InstanceId == contributor.InstanceId))
                continue;
            _contributors.Add(contributor);
        }
    }
}
