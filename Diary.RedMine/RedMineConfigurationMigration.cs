using Diary.PluginBase;
using Newtonsoft.Json.Linq;

namespace Diary.RedMine;

/// <summary>将 Redmine 旧的单实例根配置升级为实例列表配置。</summary>
public sealed class RedMineConfigurationMigration : IPluginConfigurationMigration
{
    public string PluginId => RedMinePluginConstants.PluginId;
    public int FromVersion => 0;
    public int ToVersion => 1;

    public object Migrate(object configuration)
    {
        if (configuration is not JObject payload)
            throw new InvalidOperationException("Redmine 配置必须是 JSON 对象");

        if (payload["Instances"] is JArray instances)
        {
            if (instances.Count == 0)
                instances.Add(CreateDefaultInstance(payload));
            EnsureInstanceIds(instances);
            return payload;
        }

        payload["Instances"] = new JArray(CreateDefaultInstance(payload));
        return payload;
    }

    private static JObject CreateDefaultInstance(JObject payload)
    {
        var instance = new JObject
        {
            ["InstanceId"] = RedMinePluginConstants.DefaultInstanceId,
            ["DisplayName"] = "RedMine工具",
            ["Enabled"] = true,
        };
        foreach (var propertyName in new[]
                 {
                     "RedMineServerUrl",
                     "RedMineApiKey",
                     "EnableProxy",
                     "ProxyServer",
                 })
        {
            if (payload[propertyName] is JToken value)
                instance[propertyName] = value.DeepClone();
        }

        return instance;
    }

    private static void EnsureInstanceIds(JArray instances)
    {
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in instances.OfType<JObject>())
        {
            var instanceId = (string?)item["InstanceId"];
            if (string.IsNullOrWhiteSpace(instanceId) || !usedIds.Add(instanceId))
            {
                var candidate = RedMinePluginConstants.DefaultInstanceId;
                var suffix = 1;
                while (!usedIds.Add(candidate))
                    candidate = $"{RedMinePluginConstants.DefaultInstanceId}.{suffix++}";
                item["InstanceId"] = candidate;
            }

            item["DisplayName"] ??= "RedMine工具";
            item["Enabled"] ??= true;
        }
    }
}
