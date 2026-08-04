using Diary.GUIBase;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.RedMine;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.RedMine.UI.ViewModels;

[DiAutoRegister(singleton: true, serviceType: typeof(ITrackerUiContribution))]
public sealed class RedMineTrackerIntegration : ITrackerInstance, ITrackerUiContribution
{
    private readonly IServiceProvider _services;
    private readonly IRedMineUiData _data;
    private readonly IRedMineApi _api;

    public RedMineTrackerIntegration(IServiceProvider services, IRedMineUiData data, IRedMineApi api)
    {
        _services = services;
        _data = data;
        _api = api;
    }

    public string PluginId => "tracker.redmine";
    public string InstanceId => "redmine.default";
    public string DisplayName => "RedMine工具";
    public string Icon => "fa-cloud";
    public bool IsConfigured => RedMineConfigurationStore.Current.Valid();

    public IDictionary<int, object?>? LoadBindingsByDate(string date)
    {
        var entries = BaseApp.Instance.UseDb?.GetExtension<IRedMineDb>()?.GetWorkTimeEntriesByDate(date);
        return entries?.ToDictionary(item => item.Key, item => (object?)item.Value);
    }

    public ITrackerInstance Instance => this;
    public ViewModelBase? CreateSettingsPage(object configuration) => null;
    public ViewModelBase? CreateManagementPage(string instanceId)
        => _services.GetService<RedMineManageViewModel>();
    public ITrackerEditorExtension? CreateEditorExtension(string instanceId)
        => new RedMineEditorRegionViewModel(_data, _api);
}
