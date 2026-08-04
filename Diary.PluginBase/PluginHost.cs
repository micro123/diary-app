using Microsoft.Extensions.DependencyInjection;

namespace Diary.PluginBase;

public sealed record PluginLoadResult(PluginState State, string? Error = null);

public static class PluginHost
{
    public static PluginLoadResult Register(
        ITrackerPlugin plugin,
        PluginCompatibilityContext compatibility,
        IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(services);

        if (!PluginCompatibilityValidator.Validate(plugin.Manifest, compatibility, out var error))
            return new PluginLoadResult(PluginState.Blocked, error);

        try
        {
            plugin.RegisterServices(services);
            return new PluginLoadResult(PluginState.Compatible);
        }
        catch (Exception ex)
        {
            return new PluginLoadResult(PluginState.Blocked, ex.Message);
        }
    }
}
