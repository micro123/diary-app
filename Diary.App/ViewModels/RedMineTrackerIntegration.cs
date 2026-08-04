using Diary.App.Models;
using Diary.Core.Constants;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.PluginUI;
using Diary.RedMine;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.App.ViewModels;

/// <summary>
/// RedMine 作为第一个 tracker 实现，同时实现 <see cref="ITrackerInstance"/>（元数据 + 批量绑定）
/// 与 <see cref="ITrackerUiContribution"/>（UI 贡献）。经 DI 注册为 <c>ITrackerUiContribution</c> 单例
/// （<c>[DiAutoRegister(singleton:true, serviceType:typeof(ITrackerUiContribution))]</c>），
/// 调用方经 <see cref="ITrackerUiContribution.Instance"/> 取实例元数据，无需多接口解析。
/// 后续 RedMine 外部化为独立插件时，再拆 <c>ITrackerPlugin</c>+manifest（文档阶段 3）。
/// </summary>
[DiAutoRegister(singleton: true, serviceType: typeof(ITrackerUiContribution))]
public class RedMineTrackerIntegration : ITrackerInstance, ITrackerUiContribution
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

    // ---- ITrackerInstance ----
    public string PluginId => "tracker.redmine";
    public string InstanceId => "redmine.default";
    public string DisplayName => PageNames.RedMineTool;
    public string Icon => "fa-cloud";
    public bool IsConfigured => App.Instance.AppConfig.RedMineSettings.Valid();

    public IDictionary<int, object?>? LoadBindingsByDate(string date)
    {
        var entries = App.Instance.UseDb?.RedMineDb?.GetWorkTimeEntriesByDate(date);
        if (entries is null)
            return null;
        var dict = new Dictionary<int, object?>();
        foreach (var kv in entries)
            dict[kv.Key] = kv.Value;
        return dict;
    }

    // ---- ITrackerUiContribution ----
    public ITrackerInstance Instance => this;

    public ViewModelBase? CreateSettingsPage(object configuration) => null; // 配置迁移是后续增量
    public ViewModelBase? CreateManagementPage(string instanceId) => _services.GetService<RedMineManageViewModel>();
    public ITrackerEditorExtension? CreateEditorExtension(string instanceId) => new RedMineEditorRegionViewModel(_shareData, _api);
}
