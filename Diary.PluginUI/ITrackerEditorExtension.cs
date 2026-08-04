using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;

namespace Diary.PluginUI;

/// <summary>
/// 工作项编辑器扩展区（文档 §10）。一个 tracker 实例贡献一个扩展，编辑器聚合多个。
/// 替代现有 <c>ITrackerEditorRegion</c>（迁入后该接口废弃）。
/// </summary>
public interface ITrackerEditorExtension
{
    TrackerKey Key { get; }
    string InstanceId { get; }

    /// <summary>扩展区 UI 的 ViewModel，由编辑器 ContentControl 宿主。</summary>
    ViewModelBase View { get; }

    /// <summary>加载工作项的本地绑定（<paramref name="binding"/> 为批量预取，null 时扩展自行加载；新项 item 为 null）。</summary>
    void Load(WorkItem? item, object? binding = null);

    /// <summary>保存时持久化本地绑定（如 CreateWorkTimeEntry）；无绑定时返回 true。</summary>
    bool Save(WorkItem item);

    /// <summary>复制当前选择到另一扩展（重复工作项用；target 可能为 null）。</summary>
    void CloneTo(ITrackerEditorExtension? target);

    /// <summary>是否锁住核心编辑字段（如已上传到远程）。</summary>
    bool IsLocked { get; }

    /// <summary>核心工作项是否可删除（如未上传才可删）。</summary>
    bool CanDelete { get; }

    /// <summary>上传到远程，返回统一结果。</summary>
    Task<TrackerOperationResult> UploadAsync(WorkItem item);

    /// <summary>应用 tracker 自己解释的模板数据，核心编辑器不解析 payload。</summary>
    void ApplyTemplateData(object data);
}

/// <summary>
/// tracker 的 UI 贡献（文档 §9）：配置页、管理页、编辑器扩展。
/// 由主程序按已启用实例解析并挂载。
/// </summary>
public interface ITrackerUiContribution
{
    string PluginId { get; }

    /// <summary>本贡献对应的 tracker 实例（元数据 + 批量绑定）。同一对象时 Instance => this。</summary>
    ITrackerInstance Instance { get; }

    ViewModelBase? CreateSettingsPage(object configuration);
    ViewModelBase? CreateManagementPage(string instanceId);
    ITrackerEditorExtension? CreateEditorExtension(string instanceId);
}
