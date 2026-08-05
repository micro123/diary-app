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

/// <summary>带插件和实例身份的注册表条目，供诊断和管理 UI 使用。</summary>
public sealed record PluginInstanceRegistryEntry(
    string PluginId,
    string InstanceId,
    PluginInstanceEntry Entry);

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

    /// <summary>所有带稳定身份的条目，避免诊断页依赖字典枚举顺序或实例显示名。</summary>
    public IReadOnlyCollection<PluginInstanceRegistryEntry> AllEntriesWithIdentity
        => _entries.Select(pair => new PluginInstanceRegistryEntry(
            pair.Key.PluginId,
            pair.Key.InstanceId,
            pair.Value)).ToList();

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

    /// <summary>
    /// 清除某实例的非 Enabled 条目（失败/禁用），使重注册可达。
    /// 已启用实例不在此清除，避免误删正在使用的实例。
    /// </summary>
    public bool Clear(string pluginId, string instanceId)
    {
        var key = (pluginId, instanceId);
        if (!_entries.TryGetValue(key, out var entry))
            return false;
        if (entry.State == TrackerInstanceState.Enabled)
            return false;
        return _entries.Remove(key);
    }

    public bool Disable(string pluginId, string instanceId)
    {
        var key = (pluginId, instanceId);
        if (!_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = new PluginInstanceEntry(null, TrackerInstanceState.Disabled, null);
            return true;
        }

        if (entry.State == TrackerInstanceState.Disabled)
            return true;
        if (entry.State != TrackerInstanceState.Enabled)
            return false;

        _entries[key] = new PluginInstanceEntry(null, TrackerInstanceState.Disabled, null);
        return true;
    }

    /// <summary>清空当前生命周期快照，供数据库重载后重新创建实例。</summary>
    public void ClearAll() => _entries.Clear();

    public PluginInstanceEntry? GetEntry(string pluginId, string instanceId)
        => _entries.GetValueOrDefault((pluginId, instanceId));

    /// <summary>返回已启用实例，否则 null。</summary>
    public ITrackerInstance? Get(string pluginId, string instanceId)
        => _entries.GetValueOrDefault((pluginId, instanceId)) is { State: TrackerInstanceState.Enabled, Instance: var inst } ? inst : null;
}
