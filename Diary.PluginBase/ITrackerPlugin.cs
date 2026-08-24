using Microsoft.Extensions.DependencyInjection;

namespace Diary.PluginBase;

/// <summary>
/// tracker 插件入口（文档 §9）。插件程序集暴露一个本接口实现，主程序发现后
/// 据此注册服务、创建配置、获取迁移、按实例创建 tracker。
/// 本接口不依赖 UI；UI 贡献由 <c>Diary.PluginUI.ITrackerUiContribution</c> 提供。
/// </summary>
public interface ITrackerPlugin : IPluginInstanceConfigurationStore
{
    PluginManifest Manifest { get; }

    /// <summary>向主程序 DI 容器注册插件内部服务。</summary>
    void RegisterServices(IServiceCollection services);

    /// <summary>创建插件配置对象的默认实例（由主程序持久化）。</summary>
    object CreateConfiguration();

    /// <summary>插件数据库迁移链（按 FromVersion→ToVersion 排序由主程序调度）。</summary>
    IEnumerable<IPluginMigration> GetMigrations();

    /// <summary>插件配置 JSON schema 迁移链；默认空实现保持旧插件兼容。</summary>
    IEnumerable<IPluginConfigurationMigration> GetConfigurationMigrations()
        => Array.Empty<IPluginConfigurationMigration>();

    /// <summary>按实例 ID + 配置创建一个 tracker 实例。</summary>
    ITrackerInstance CreateInstance(string instanceId, object configuration);

    /// <summary>从宿主上下文读取插件配置并生成启用的实例注册项。</summary>
    IEnumerable<PluginInstanceRegistration> GetInstanceRegistrations(object hostContext);

    /// <summary>删除指定实例的插件数据。默认不支持删除，避免卸载时误删数据。</summary>
    bool TryDeleteInstanceData(PluginHostContext hostContext, string instanceId)
        => false;
}

/// <summary>
/// tracker 实例（文档 §9）。一个插件类型可有多个实例（如公司/个人 Redmine）。
/// </summary>
public interface ITrackerInstance
{
    string PluginId { get; }
    string InstanceId { get; }
    string DisplayName { get; }
    /// <summary>导航页图标键（如 "fa-cloud"）。</summary>
    string Icon { get; }
    bool IsConfigured { get; }

    /// <summary>
    /// 批量加载某日所有工作项的本地绑定（装箱为 object，由插件解释）。
    /// 供编辑器列表批量同步；无数据返回 null。
    /// </summary>
    IDictionary<int, object?>? LoadBindingsByDate(string date);

    /// <summary>
    /// 使用宿主已经加载的工作项 ID 批量读取绑定。默认回退到按日期读取，保持旧插件兼容。
    /// </summary>
    IDictionary<int, object?>? LoadBindingsByDate(
        string date,
        IReadOnlyCollection<int> workItemIds)
        => LoadBindingsByDate(date);
}
