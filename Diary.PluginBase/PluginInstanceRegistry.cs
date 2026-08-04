namespace Diary.PluginBase;

public sealed record PluginInstanceCreationResult(
    bool Success,
    ITrackerInstance? Instance = null,
    string? Error = null);

public sealed class PluginInstanceRegistry
{
    private readonly Dictionary<(string PluginId, string InstanceId), ITrackerInstance> _instances = new();

    public IReadOnlyCollection<ITrackerInstance> Instances => _instances.Values;

    public PluginInstanceCreationResult Create(
        ITrackerPlugin plugin,
        string instanceId,
        object configuration)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(configuration);

        var key = (plugin.Manifest.Id, instanceId);
        if (_instances.ContainsKey(key))
            return new PluginInstanceCreationResult(false, Error: "插件实例已存在");

        try
        {
            var instance = plugin.CreateInstance(instanceId, configuration);
            if (instance.PluginId != plugin.Manifest.Id || instance.InstanceId != instanceId)
                return new PluginInstanceCreationResult(false, Error: "插件实例身份不匹配");
            _instances.Add(key, instance);
            return new PluginInstanceCreationResult(true, instance);
        }
        catch (Exception ex)
        {
            return new PluginInstanceCreationResult(false, Error: ex.Message);
        }
    }

    public ITrackerInstance? Get(string pluginId, string instanceId)
        => _instances.GetValueOrDefault((pluginId, instanceId));
}
