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
                instances.Add(CreateDefaultInstance(payload, enabled: HasLegacyConfiguration(payload)));
            EnsureInstanceIds(instances);
            return payload;
        }

        // 旧配置只有根级 Redmine 字段，说明用户已经配置过该 tracker；
        // 迁移出的唯一默认实例应保持可用，而不是被当成首次安装的空实例。
        payload["Instances"] = new JArray(CreateDefaultInstance(payload, enabled: true));
        return payload;
    }

    private static JObject CreateDefaultInstance(JObject payload, bool enabled)
    {
        var instance = new JObject
        {
            ["InstanceId"] = RedMinePluginConstants.DefaultInstanceId,
            ["DisplayName"] = "RedMine工具",
            ["Enabled"] = enabled,
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

    private static bool HasLegacyConfiguration(JObject payload)
        => !string.IsNullOrWhiteSpace((string?)payload["RedMineServerUrl"])
            || !string.IsNullOrWhiteSpace((string?)payload["RedMineApiKey"]);

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
            item["Enabled"] ??= false;
        }
    }
}
