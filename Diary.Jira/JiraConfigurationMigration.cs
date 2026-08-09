using Diary.PluginBase;
using Newtonsoft.Json.Linq;

namespace Diary.Jira;

public sealed class JiraConfigurationMigration : IPluginConfigurationMigration
{
    public string PluginId => JiraPluginConstants.PluginId;
    public int FromVersion => 0;
    public int ToVersion => 1;

    public object Migrate(object configuration)
    {
        if (configuration is not JObject payload)
            throw new ArgumentException("Jira 配置必须是 JSON 对象", nameof(configuration));
        payload["Instances"] ??= new JArray();
        return payload;
    }
}
