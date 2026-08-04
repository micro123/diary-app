using Diary.App.ViewModels.Dialogs;
using Diary.Core.Data.AppConfig;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.App.ViewModels;

[DiAutoRegister(singleton: true, serviceType: typeof(ITrackerConfigurationProvider))]
public sealed class RedMineConfigurationProvider(IServiceProvider services) : ITrackerConfigurationProvider
{
    public string PluginId => "tracker.redmine";

    public object CreateDefaultConfiguration() => new RedMineConfig();

    public bool Validate(object configuration, out string? error)
    {
        if (configuration is RedMineConfig config && config.Valid())
        {
            error = null;
            return true;
        }

        error = "RedMine 服务地址、API Key 或代理配置无效";
        return false;
    }

    public ViewModelBase CreateSettingsPage(object configuration)
    {
        var viewModel = services.GetRequiredService<GenericConfigViewModel>();
        viewModel.InitSettings("RedMine设置", configuration);
        return viewModel;
    }
}
