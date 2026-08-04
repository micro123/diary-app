using Diary.Core.Utils;
using Diary.PluginBase;

namespace Diary.App;

/// <summary>
/// 主程序统一创建并加载插件配置。配置格式和迁移细节由插件配置类型负责声明。
/// </summary>
public sealed class PluginConfigurationLoader
{
    public object Load(ITrackerPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        var configuration = plugin.CreateConfiguration();
        EasySaveLoad.Load(configuration);
        return configuration;
    }
}
