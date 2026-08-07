using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Diary.GUIBase.ViewModels;
using Diary.GUIBase;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.PluginUI;
using Diary.Utils;
using Irihi.Avalonia.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Diary.App.ViewModels.Dialogs;

[DiAutoRegister]
public partial class TrackerSettingsDialogViewModel : ViewModelBase, IDialogContext
{
    private readonly IReadOnlyList<(object Configuration, ITrackerConfigurationProvider Provider)> _pluginSettings;
    private readonly PluginConfigurationLoader _configurationLoader = new();
    private readonly ILogger _logger;

    public ObservableCollection<ViewModelBase> Pages { get; } = new();

    public TrackerSettingsDialogViewModel(
        IEnumerable<ITrackerConfigurationProvider> providers,
        ILogger logger)
    {
        _logger = logger;
        _pluginSettings = LoadPluginSettings(providers);
        foreach (var (configuration, provider) in _pluginSettings)
        {
            var page = provider.CreateSettingsPage(configuration);
            if (page is not null)
                Pages.Add(page);
        }
    }

    public event EventHandler<object?>? RequestClose;

    public void Close() => RequestClose?.Invoke(this, null);

    [RelayCommand]
    private void Save()
    {
        foreach (var page in Pages.OfType<ITrackerSettingsPage>())
            page.Save();

        var succeeded = true;
        foreach (var (configuration, provider) in _pluginSettings)
        {
            if (!provider.Validate(configuration, out var error))
            {
                _logger.LogWarning("Tracker 配置校验失败：{PluginId}，{Error}", provider.PluginId, error);
                succeeded = false;
                continue;
            }

            var plugin = (BaseApp.Instance as App)?.Plugins
                .FirstOrDefault(item => item.Manifest.Id == provider.PluginId);
            if (plugin is null || !_configurationLoader.Save(plugin, configuration))
                succeeded = false;
        }

        if (!succeeded)
            return;

        NotificationManager?.Show("Tracker 配置已保存");
        EventDispatcher.Msg(new ConfigUpdateEvent());
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);

    private static IReadOnlyList<(object Configuration, ITrackerConfigurationProvider Provider)> LoadPluginSettings(
        IEnumerable<ITrackerConfigurationProvider> providers)
    {
        if (BaseApp.Instance is not App app)
            return Array.Empty<(object, ITrackerConfigurationProvider)>();

        return providers
            .Select(provider => app.PluginConfigurations.TryGetValue(provider.PluginId, out var configuration)
                ? (Configuration: configuration, Provider: provider)
                : ((object Configuration, ITrackerConfigurationProvider Provider)?)null)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .ToArray();
    }
}
