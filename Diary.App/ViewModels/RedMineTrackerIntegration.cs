using Diary.App.Models;
using Diary.Core.Constants;
using Diary.GUIBase;
using Diary.GUIBase.ViewModels;
using Diary.RedMine;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.App.ViewModels;

/// <summary>
/// RedMine 作为第一个 <see cref="ITrackerIntegration"/> 实现。经 DI 注册为
/// <c>ITrackerIntegration</c> 单例（<c>[DiAutoRegister(singleton:true, serviceType:typeof(ITrackerIntegration))]</c>），
/// 编辑器/导航据此扩展。后续第二个 tracker（Jira 等）只需再实现本接口并注册。
/// </summary>
[DiAutoRegister(singleton: true, serviceType: typeof(ITrackerIntegration))]
public class RedMineTrackerIntegration : ITrackerIntegration
{
    private readonly IServiceProvider _services;
    private readonly DbShareData _shareData;
    private readonly IRedMineApi _api;

    public RedMineTrackerIntegration(IServiceProvider services, DbShareData shareData, IRedMineApi api)
    {
        _services = services;
        _shareData = shareData;
        _api = api;
    }

    public string Key => "RedMine";
    public string DisplayName => PageNames.RedMineTool;
    public string Icon => "fa-cloud";
    public bool IsConfigured => App.Instance.AppConfig.RedMineSettings.Valid();

    public ITrackerEditorRegion? CreateEditorRegion() => new RedMineEditorRegionViewModel(_shareData, _api);

    public IDictionary<int, object?>? LoadBindingsByDate(string date)
    {
        var entries = App.Instance.UseDb?.GetWorkTimeEntriesByDate(date);
        if (entries is null)
            return null;
        var dict = new Dictionary<int, object?>();
        foreach (var kv in entries)
            dict[kv.Key] = kv.Value;
        return dict;
    }

    public ViewModelBase? CreateManagePage() => _services.GetService<RedMineManageViewModel>();
}
