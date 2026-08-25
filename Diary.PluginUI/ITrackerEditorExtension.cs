using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;

namespace Diary.PluginUI;

public sealed record TrackerUploadValidation(bool CanUpload, string? Error = null)
{
    public static TrackerUploadValidation Valid { get; } = new(true);
    public static TrackerUploadValidation Unsupported { get; } = new(
        false,
        "Tracker 未提供快捷批量同步所需的完整性校验");

    public static TrackerUploadValidation Invalid(string error) => new(false, error);
}

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

    /// <summary>
    /// 使用批量预取结果加载工作项。此时 <paramref name="binding"/> 为 null 表示已确认不存在绑定，
    /// 不应再逐项查询数据库。默认实现保留第三方扩展的兼容行为。
    /// </summary>
    void LoadFromBatch(WorkItem? item, object? binding) => Load(item, binding);

    /// <summary>保存时持久化本地绑定（如 CreateWorkTimeEntry）；无绑定时返回 true。</summary>
    bool Save(WorkItem item);

    /// <summary>
    /// 当前编辑值是否与最近一次加载或保存的本地绑定不同。
    /// 未实现该能力的扩展默认返回 true，以避免自动切换事项时遗漏保存。
    /// </summary>
    bool HasChanges => true;

    /// <summary>复制当前选择到另一扩展（重复工作项用；target 可能为 null）。</summary>
    void CloneTo(ITrackerEditorExtension? target);

    /// <summary>是否锁住核心编辑字段（如已上传到远程）。</summary>
    bool IsLocked { get; }

    /// <summary>最近一次远程上传状态，随本地 Tracker 绑定持久化。</summary>
    TrackerUploadState UploadState => TrackerUploadState.NotAttempted;

    /// <summary>最近一次上传错误；不包含敏感凭据。</summary>
    string? UploadError => null;

    /// <summary>最近一次上传尝试时间。</summary>
    DateTimeOffset? UploadAttemptedAt => null;

    /// <summary>Tracker 扩展是否允许无确认删除；核心编辑器删除时仍需根据上传状态提示用户。</summary>
    bool CanDelete { get; }

    /// <summary>
    /// 在执行无需人工确认的批量同步前检查当前本地绑定是否具备远程上传所需信息。
    /// 默认不参与快捷批量同步；Tracker 必须显式覆盖并校验自己的必填字段和失效值。
    /// </summary>
    TrackerUploadValidation ValidateUpload(WorkItem item) => TrackerUploadValidation.Unsupported;

    /// <summary>上传到远程，返回统一结果。</summary>
    Task<TrackerOperationResult> UploadAsync(WorkItem item);

}

/// <summary>可选的标签默认值能力。只应用当前 Tracker 实例自己的编辑器字段。</summary>
public interface ITrackerTagDefaults
{
    TrackerTagDefaultsResult ApplyTagDefaults(WorkTag tag);
}

public sealed record TrackerTagDefaultConflict(
    string Field,
    IReadOnlyCollection<string> RuleIds);

public sealed record TrackerTagDefaultInvalidTarget(
    string Field,
    string TargetId,
    string RuleId);

public sealed record TrackerTagDefaultsResult(
    IReadOnlyCollection<string> ChangedFields,
    IReadOnlyCollection<TrackerTagDefaultConflict> Conflicts,
    IReadOnlyCollection<TrackerTagDefaultInvalidTarget> InvalidTargets)
{
    public static TrackerTagDefaultsResult Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<TrackerTagDefaultConflict>(),
        Array.Empty<TrackerTagDefaultInvalidTarget>());
}

public interface ITagRuleEditorContribution
{
    string PluginId { get; }
    string InstanceId { get; }
    string InstanceName { get; }
    ViewModelBase View { get; }
    void SelectTag(WorkTag tag);
    IReadOnlyCollection<TrackerTagRulePackageItem> ExportRules(
        IReadOnlyDictionary<int, string> tagKeys);
    IReadOnlyCollection<TrackerTagRuleValidation> ValidateImportRules(
        IReadOnlyCollection<TrackerTagRulePackageItem> rules,
        IReadOnlyDictionary<string, int> tagIds);
    int ImportRules(
        IReadOnlyCollection<TrackerTagRulePackageItem> rules,
        IReadOnlyDictionary<string, int> tagIds);
    void Commit();
    void Reload();
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
    ITagRuleEditorContribution? CreateTagRuleEditorContribution() => null;
}

public interface ITrackerUiContributionFactory
{
    string PluginId { get; }
    ITrackerUiContribution Create(ITrackerInstance instance);
}
