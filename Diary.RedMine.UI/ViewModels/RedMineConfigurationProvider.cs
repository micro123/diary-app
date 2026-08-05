using Diary.GUIBase.ViewModels;
using Diary.GUIBase.ViewModels.Dialogs;
using Diary.PluginUI;
using Diary.RedMine;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.RedMine.UI.ViewModels;

[DiAutoRegister(singleton: true, serviceType: typeof(ITrackerConfigurationProvider))]
public sealed class RedMineConfigurationProvider(IServiceProvider services) : ITrackerConfigurationProvider
{
    public string PluginId => RedMinePluginConstants.PluginId;
    public object CreateDefaultConfiguration() => new RedMinePluginConfig();

    public bool Validate(object configuration, out string? error)
    {
        if (configuration is RedMinePluginConfig config
            && config.Instances.Any(instance => instance.Enabled && instance.Valid()))
        {
            error = null;
            return true;
        }
        error = "RedMine 服务地址、API Key 或代理配置无效";
        return false;
    }

    public ViewModelBase? CreateSettingsPage(object configuration)
    {
        if (configuration is not RedMinePluginConfig config)
            return null;

        var viewModel = services.GetRequiredService<RedMineConfigurationViewModel>();
        viewModel.InitSettings(config);
        return viewModel;
    }

}
