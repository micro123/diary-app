using Diary.Core.Data.AppConfig;
using Diary.Core.Configure;
using Diary.Core.Utils;
using Newtonsoft.Json.Linq;

namespace Diary.RedMine;

[StorageFile("redmine_settings.json", "diary.redmine")]
public sealed class RedMinePluginConfig : RedMineConfig
{
}

public static class RedMineConfigurationStore
{
    private static readonly Lazy<RedMinePluginConfig> CurrentHolder = new(Load);

    public static RedMinePluginConfig Current => CurrentHolder.Value;

    private static RedMinePluginConfig Load()
    {
        var configuration = new RedMinePluginConfig();
        EasySaveLoad.Load(configuration);

        if (!configuration.Valid()
            && AllConfig.Instance.ExtensionData.TryGetValue("RedMineSettings", out JToken? legacyToken))
        {
            var legacy = legacyToken.ToObject<RedMineConfig>();
            if (legacy is not null && legacy.Valid())
            {
                Copy(legacy, configuration);
                EasySaveLoad.Save(configuration);
            }
        }

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
