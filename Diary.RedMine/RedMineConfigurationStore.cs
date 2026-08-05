using Diary.Core.Data.AppConfig;
using Diary.Core.Configure;
using Diary.Core.Utils;
using Newtonsoft.Json.Linq;

namespace Diary.RedMine;

[StorageFile("redmine_settings.json", "diary.redmine")]
public sealed class RedMinePluginConfig : RedMineConfig
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
    private static readonly Lazy<RedMinePluginConfig> CurrentHolder = new(Load);

    public static RedMinePluginConfig Current => CurrentHolder.Value;

    private static RedMinePluginConfig Load()
    {
        var configuration = new RedMinePluginConfig();
        var migratedLegacy = false;
        if (!configuration.Valid()
            && AllConfig.Instance.ExtensionData.TryGetValue("RedMineSettings", out JToken? legacyToken))
        {
            var legacy = legacyToken.ToObject<RedMineConfig>();
            if (legacy is not null && legacy.Valid())
            {
                Copy(legacy, configuration);
                AllConfig.Instance.ExtensionData.Remove("RedMineSettings");
                migratedLegacy = true;
            }
        }

        if (configuration.Instances.Count == 0)
        {
            var defaultInstance = new RedMineInstanceSettings
            {
                InstanceId = RedMinePluginConstants.DefaultInstanceId,
                DisplayName = "RedMine工具",
                Enabled = migratedLegacy,
            };
            Copy(configuration, defaultInstance);
            configuration.Instances.Add(defaultInstance);
        }

        // 配置文件的读取和 schema 解包由宿主 PluginConfigurationLoader 负责。
        // 这里仅处理旧的 AllConfig 配置和首次启动的内存默认值，避免把包外层
        // 当成 RedMinePluginConfig 读取后覆盖 Payload 中的实例启用状态。
        if (migratedLegacy)
            EasySaveLoad.Save(configuration);
        if (migratedLegacy)
            EasySaveLoad.Save(AllConfig.Instance);

        return configuration;
    }

    private static void Copy(RedMineConfig source, RedMineConfig target)
    {
        target.RedMineServerUrl = source.RedMineServerUrl;
        target.RedMineApiKey = source.RedMineApiKey;
        target.EnableProxy = source.EnableProxy;
        target.ProxyServer = source.ProxyServer;
    }
}
