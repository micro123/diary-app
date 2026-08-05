namespace Diary.PluginBase;

/// <summary>
/// 插件向宿主声明的实例注册项。<see cref="State"/> 为非 <see cref="TrackerInstanceState.Enabled"/>
/// 时表示该实例无法启用，<see cref="Configuration"/> 可能为 null，宿主不应调用 <c>CreateInstance</c>。
/// </summary>
public sealed record PluginInstanceRegistration(
    string InstanceId,
    object? Configuration,
    TrackerInstanceState State = TrackerInstanceState.Enabled,
    string? Error = null);
