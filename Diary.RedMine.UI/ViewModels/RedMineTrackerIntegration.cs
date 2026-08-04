using Diary.GUIBase;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.RedMine;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.RedMine.UI.ViewModels;

[DiAutoRegister(singleton: true, serviceType: typeof(ITrackerUiContributionFactory))]
public sealed class RedMineTrackerIntegrationFactory(IServiceProvider services) : ITrackerUiContributionFactory
{
    public string PluginId => RedMinePluginConstants.PluginId;

    public ITrackerUiContribution Create(ITrackerInstance instance)
    {
        if (instance is not RedMineInstance redmine)
            throw new ArgumentException("RedMine instance is required", nameof(instance));
        var data = new RedMineUiDataStore(
            Logging.Logger, redmine.InstanceId, redmine.Database);
        data.InitLoad();
        return new RedMineTrackerIntegration(services, instance, data,
            new RedMineApi(redmine.Configuration), redmine.Database);
    }
}

public sealed class RedMineTrackerIntegration : ITrackerUiContribution
{
    private readonly IServiceProvider _services;
    private readonly ITrackerInstance _instance;
    private readonly IRedMineUiData _data;
    private readonly IRedMineApi _api;
    private readonly IRedMineDb _database;

    public RedMineTrackerIntegration(
        IServiceProvider services,
        ITrackerInstance instance,
        IRedMineUiData data,
        IRedMineApi api,
        IRedMineDb database)
    {
        _services = services;
        _instance = instance;
        _data = data;
        _api = api;
        _database = database;
    }

    public string PluginId => RedMinePluginConstants.PluginId;
    public ITrackerInstance Instance => _instance;
    public ViewModelBase? CreateSettingsPage(object configuration) => null;
    public ViewModelBase? CreateManagementPage(string instanceId)
        => ActivatorUtilities.CreateInstance<RedMineManageViewModel>(_services, _api);
    public ITrackerEditorExtension? CreateEditorExtension(string instanceId)
        => new RedMineEditorRegionViewModel(_data, _api, _database);
}
