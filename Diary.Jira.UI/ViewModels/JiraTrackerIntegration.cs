using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.Utils;

namespace Diary.Jira.UI.ViewModels;

[DiAutoRegister(singleton: true, serviceType: typeof(ITrackerUiContributionFactory))]
public sealed class JiraTrackerIntegrationFactory : ITrackerUiContributionFactory
{
    public string PluginId => JiraPluginConstants.PluginId;

    public ITrackerUiContribution Create(ITrackerInstance instance)
    {
        if (instance is not JiraInstance jira)
            throw new ArgumentException("Jira instance is required", nameof(instance));
        var data = new JiraUiDataStore(jira.Database);
        data.InitLoad();
        return new JiraTrackerIntegration(instance, data, new JiraApi(jira.Settings), jira.Database, jira.Settings);
    }
}

public sealed class JiraTrackerIntegration(
    ITrackerInstance instance,
    JiraUiDataStore data,
    IJiraApi api,
    IJiraDb database,
    JiraInstanceSettings settings) : ITrackerUiContribution, IDisposable
{
    public string PluginId => JiraPluginConstants.PluginId;
    public ITrackerInstance Instance => instance;
    public ViewModelBase? CreateSettingsPage(object configuration) => null;
    public ViewModelBase? CreateManagementPage(string instanceId) => null;
    public ITrackerEditorExtension? CreateEditorExtension(string instanceId)
        => instanceId == settings.InstanceId
            ? new JiraEditorRegionViewModel(data, api, database, settings)
            : null;

    public void Dispose()
    {
        if (api is IDisposable disposable)
            disposable.Dispose();
    }
}
