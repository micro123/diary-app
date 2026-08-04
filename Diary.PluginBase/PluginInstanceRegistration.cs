namespace Diary.PluginBase;

public sealed record PluginInstanceRegistration(
    string InstanceId,
    object Configuration);
