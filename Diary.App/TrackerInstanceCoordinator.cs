using Diary.PluginBase;
using Microsoft.Extensions.Logging;

namespace Diary.App;

public sealed class TrackerInstanceCoordinator(
    PluginInstanceRegistry registry,
    ILogger<TrackerInstanceCoordinator> logger)
{
    public void Register(
        ITrackerPlugin plugin,
        IEnumerable<PluginInstanceRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            if (registry.GetEntry(plugin.Manifest.Id, registration.InstanceId) is not null)
                continue;

            if (registration.State == TrackerInstanceState.Enabled)
            {
                var result = registry.Create(plugin, registration.InstanceId, registration.Configuration!);
                if (result.Success)
                {
                    logger.LogInformation(
                        "Tracker instance {PluginId}/{InstanceId} enabled",
                        plugin.Manifest.Id, registration.InstanceId);
                }
                else
                {
                    logger.LogError(
                        "Tracker instance {PluginId}/{InstanceId} blocked: {Error}",
                        plugin.Manifest.Id, registration.InstanceId, result.Error);
                }
            }
            else
            {
                registry.Record(plugin.Manifest.Id, registration.InstanceId, registration.State, registration.Error);
                logger.LogWarning(
                    "Tracker instance {PluginId}/{InstanceId} {State}: {Error}",
                    plugin.Manifest.Id, registration.InstanceId, registration.State, registration.Error);
            }
        }
    }
}
