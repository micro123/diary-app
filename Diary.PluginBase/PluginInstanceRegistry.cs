namespace Diary.PluginBase;

public sealed record PluginInstanceCreationResult(
    bool Success,
    ITrackerInstance? Instance = null,
    string? Error = null,
    TrackerInstanceState State = TrackerInstanceState.Enabled)
{
    public static PluginInstanceCreationResult Failed(TrackerInstanceState state, string? error)
        => new(false, null, error, state);
}

/// <summary>
/// 注册表中的实例条目。<see cref="Instance"/> 在 <see cref="TrackerInstanceState.Enabled"/>
/// 之外的状态下为 null（实例未创建或创建失败）。
/// </summary>
public sealed record PluginInstanceEntry(
    ITrackerInstance? Instance,
    TrackerInstanceState State,
    string? Error);

public sealed class PluginInstanceRegistry
{
    private readonly Dictionary<(string PluginId, string InstanceId), PluginInstanceEntry> _entries = new();

    /// <summary>已启用实例（<see cref="TrackerInstanceState.Enabled"/>），供 UI、编辑器、模板消费。</summary>
    public IReadOnlyCollection<ITrackerInstance> Instances
        => _entries.Values
            .Where(e => e.State == TrackerInstanceState.Enabled && e.Instance is not null)
            .Select(e => e.Instance!)
            .ToList();

    /// <summary>所有条目（含失败/禁用），供诊断页使用。</summary>
    public IReadOnlyCollection<PluginInstanceEntry> AllEntries => _entries.Values;

    public PluginInstanceCreationResult Create(
        ITrackerPlugin plugin,
        string instanceId,
        object configuration)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(configuration);

        var key = (plugin.Manifest.Id, instanceId);
        if (_entries.ContainsKey(key))
            return PluginInstanceCreationResult.Failed(TrackerInstanceState.Blocked, "插件实例已存在");
        if (!plugin.Manifest.SupportsMultipleInstances
            && _entries.Keys.Any(existing => existing.PluginId == plugin.Manifest.Id))
        {
            return PluginInstanceCreationResult.Failed(TrackerInstanceState.Blocked, "插件不支持多实例");
        }

        try
        {
            var instance = plugin.CreateInstance(instanceId, configuration);
            if (instance.PluginId != plugin.Manifest.Id || instance.InstanceId != instanceId)
                return PluginInstanceCreationResult.Failed(TrackerInstanceState.Blocked, "插件实例身份不匹配");
            _entries.Add(key, new PluginInstanceEntry(instance, TrackerInstanceState.Enabled, null));
            return new PluginInstanceCreationResult(true, instance);
        }
        catch (Exception ex)
        {
            _entries.Add(key, new PluginInstanceEntry(null, TrackerInstanceState.Blocked, ex.Message));
            return PluginInstanceCreationResult.Failed(TrackerInstanceState.Blocked, ex.Message);
        }
    }

    /// <summary>记录非创建型失败实例（迁移失败、扩展缺失等），已存在则跳过。</summary>
    public void Record(string pluginId, string instanceId, TrackerInstanceState state, string? error)
    {
        var key = (pluginId, instanceId);
        if (_entries.ContainsKey(key))
            return;
        _entries.Add(key, new PluginInstanceEntry(null, state, error));
    }

    public PluginInstanceEntry? GetEntry(string pluginId, string instanceId)
        => _entries.GetValueOrDefault((pluginId, instanceId));

    /// <summary>返回已启用实例，否则 null。</summary>
    public ITrackerInstance? Get(string pluginId, string instanceId)
        => _entries.GetValueOrDefault((pluginId, instanceId)) is { State: TrackerInstanceState.Enabled, Instance: var inst } ? inst : null;
}
