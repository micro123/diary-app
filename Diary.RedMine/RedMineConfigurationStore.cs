using Diary.Core.Data.AppConfig;
using Diary.Core.Configure;

namespace Diary.RedMine;

[StorageFile("redmine_settings.json", "diary.redmine")]
public sealed class RedMinePluginConfig
{
    public IList<RedMineInstanceSettings> Instances { get; set; } = new List<RedMineInstanceSettings>();
}

public sealed class RedMineInstanceSettings : RedMineConfig
{
    public string InstanceId { get; set; } = RedMinePluginConstants.DefaultInstanceId;
    [ConfigureText("显示名称")]
    public string DisplayName { get; set; } = "RedMine工具";
    [ConfigureSwitch("启用此实例")]
    public bool Enabled { get; set; } = false;
}

public static class RedMineConfigurationStore
{
    private static readonly Lazy<RedMinePluginConfig> CurrentHolder = new(() => new RedMinePluginConfig
    {
        Instances = new List<RedMineInstanceSettings> { CreateDefaultInstance() },
    });

    public static RedMinePluginConfig Current => CurrentHolder.Value;

    private static RedMineInstanceSettings CreateDefaultInstance()
    {
        return new RedMineInstanceSettings
        {
            InstanceId = RedMinePluginConstants.DefaultInstanceId,
            DisplayName = "RedMine工具",
            Enabled = false,
        };
    }
}
