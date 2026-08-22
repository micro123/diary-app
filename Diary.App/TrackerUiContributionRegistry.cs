using Diary.PluginBase;
using Diary.PluginUI;

namespace Diary.App;

public sealed class TrackerUiContributionRegistry : IDisposable
{
    private readonly List<ITrackerUiContribution> _contributions = new();

    public IReadOnlyList<ITrackerUiContribution> Contributions => _contributions;

    public void Register(
        IEnumerable<ITrackerUiContributionFactory> factories,
        IEnumerable<ITrackerInstance> instances)
    {
        DisposeContributions();
        _contributions.Clear();
        foreach (var instance in instances)
        {
            var factory = factories.FirstOrDefault(x => x.PluginId == instance.PluginId);
            if (factory is null)
                continue;
            var contribution = factory.Create(instance);
            if (contribution.PluginId != instance.PluginId
                || contribution.Instance.InstanceId != instance.InstanceId)
            {
                (contribution as IDisposable)?.Dispose();
                continue;
            }
            if (_contributions.Any(x => x.PluginId == contribution.PluginId
                && x.Instance.InstanceId == contribution.Instance.InstanceId))
            {
                (contribution as IDisposable)?.Dispose();
                continue;
            }
            _contributions.Add(contribution);
        }
    }

    public void Dispose()
    {
        DisposeContributions();
        _contributions.Clear();
    }

    private void DisposeContributions()
    {
        foreach (var contribution in _contributions.OfType<IDisposable>())
            contribution.Dispose();
    }
}
