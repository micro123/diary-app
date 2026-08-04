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
            if (registry.Get(plugin.Manifest.Id, registration.InstanceId) is not null)
                continue;

            var result = registry.Create(plugin, registration.InstanceId, registration.Configuration);
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
    }
}
