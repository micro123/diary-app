using Diary.PluginBase;
using Newtonsoft.Json.Linq;

namespace Diary.RedMine;

public sealed class RedMineConfigurationMigration : IPluginConfigurationMigration
{
    public string PluginId => RedMinePluginConstants.PluginId;
    public int FromVersion => 0;
    public int ToVersion => 1;

    public object Migrate(object configuration)
    {
        if (configuration is not JObject payload)
            throw new ArgumentException("RedMine 配置必须是 JSON 对象", nameof(configuration));

        var instances = payload["Instances"] as JArray ?? new JArray();
        payload["Instances"] = instances;
        foreach (var instance in payload.SelectTokens("$.Instances[*]").Cast<JObject>())
        {
            if (instance["TagRules"] is not JArray rules)
            {
                rules = new JArray();
                instance["TagRules"] = rules;
            }
            foreach (var rule in instance.SelectTokens("$.TagRules[*]").Cast<JObject>())
            {
                if (string.IsNullOrWhiteSpace((string?)rule["RuleId"]))
                    rule["RuleId"] = Guid.NewGuid().ToString("N");
            }
        }
        return payload;
    }
}
