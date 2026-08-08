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
    [ConfigureText("导航图标")]
    public string Icon { get; set; } = RedMinePluginConstants.DefaultIcon;
    [ConfigureSwitch("启用此实例")]
    public bool Enabled { get; set; } = false;
    public IList<RedMineTagRule> TagRules { get; set; } = new List<RedMineTagRule>();
}

public sealed class RedMineTagRule
{
    public string RuleId { get; set; } = Guid.NewGuid().ToString("N");
    public int TagId { get; set; }
    public int? ActivityId { get; set; }
    public int? IssueId { get; set; }
    public bool Enabled { get; set; } = true;
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
            Icon = RedMinePluginConstants.DefaultIcon,
            Enabled = false,
        };
    }
}
