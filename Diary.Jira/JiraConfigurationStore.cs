using Diary.Core.Configure;

namespace Diary.Jira;

[StorageFile("jira_settings.json", "diary.jira")]
public sealed class JiraPluginConfig
{
    public IList<JiraInstanceSettings> Instances { get; set; } = new List<JiraInstanceSettings>();
}

public sealed class JiraInstanceSettings : JiraConfig
{
    public string InstanceId { get; set; } = JiraPluginConstants.DefaultInstanceId;

    [ConfigureText("显示名称")]
    public string DisplayName { get; set; } = "Jira 工时";

    [ConfigureText("导航图标")]
    public string Icon { get; set; } = JiraPluginConstants.DefaultIcon;

    [ConfigureSwitch("启用此实例")]
    public bool Enabled { get; set; }
}

public static class JiraConfigurationStore
{
    private static readonly Lazy<JiraPluginConfig> CurrentHolder = new(() => new JiraPluginConfig
    {
        Instances = new List<JiraInstanceSettings>
        {
            new()
            {
                InstanceId = JiraPluginConstants.DefaultInstanceId,
                DisplayName = "Jira 工时",
                Icon = JiraPluginConstants.DefaultIcon,
            },
        },
    });

    public static JiraPluginConfig Current => CurrentHolder.Value;
}
