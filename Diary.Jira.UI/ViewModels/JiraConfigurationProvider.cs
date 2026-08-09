using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.Jira.UI.ViewModels;

[DiAutoRegister(singleton: true, serviceType: typeof(ITrackerConfigurationProvider))]
public sealed class JiraConfigurationProvider(
    IServiceProvider services,
    IJiraConfigurationEditService editService) : ITrackerConfigurationProvider
{
    public string PluginId => JiraPluginConstants.PluginId;
    public string DisplayName => "Jira";
    public object CreateDefaultConfiguration() => new JiraPluginConfig();

    public bool Validate(object configuration, out string? error)
    {
        if (configuration is JiraPluginConfig config
            && config.Instances.Any(instance => instance.Enabled && instance.Valid()))
        {
            error = null;
            return true;
        }
        error = "Jira 服务地址、认证方式或 API Token 无效。";
        return false;
    }

    public ViewModelBase? CreateSettingsPage(object configuration)
    {
        if (configuration is not JiraPluginConfig config)
            return null;
        var viewModel = services.GetRequiredService<JiraConfigurationViewModel>();
        viewModel.InitSettings(editService.Open(config));
        return viewModel;
    }
}
