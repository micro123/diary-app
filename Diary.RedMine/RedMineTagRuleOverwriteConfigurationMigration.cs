using Diary.PluginBase;
using Newtonsoft.Json.Linq;

namespace Diary.RedMine;

public sealed class RedMineTagRuleOverwriteConfigurationMigration : IPluginConfigurationMigration
{
    public string PluginId => RedMinePluginConstants.PluginId;
    public int FromVersion => 2;
    public int ToVersion => 3;

    public object Migrate(object configuration)
    {
        if (configuration is not JObject payload)
            throw new ArgumentException("RedMine 配置必须是 JSON 对象", nameof(configuration));

        foreach (var rule in payload.SelectTokens("$.Instances[*].TagRules[*]").OfType<JObject>())
        {
            if (rule["ForceOverwrite"] is null)
                rule["ForceOverwrite"] = false;
        }
        return payload;
    }
}
