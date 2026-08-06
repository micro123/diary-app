using Diary.PluginBase;
using Newtonsoft.Json.Linq;

namespace Diary.RedMine;

public sealed class RedMineIconConfigurationMigration : IPluginConfigurationMigration
{
    public string PluginId => RedMinePluginConstants.PluginId;
    public int FromVersion => 1;
    public int ToVersion => 2;

    public object Migrate(object configuration)
    {
        if (configuration is not JObject payload)
            throw new ArgumentException("RedMine 配置必须是 JSON 对象", nameof(configuration));

        foreach (var instance in payload.SelectTokens("$.Instances[*]").OfType<JObject>())
        {
            if (string.IsNullOrWhiteSpace((string?)instance["Icon"]))
                instance["Icon"] = RedMinePluginConstants.DefaultIcon;
        }
        return payload;
    }
}
