using Diary.Core.Data.Base;
using Diary.GUIBase.ViewModels;

namespace Diary.GUIBase;

/// <summary>
/// 工作项编辑器中的"tracker 区"生命周期。一个 tracker（如 RedMine）实现此接口，
/// 贡献编辑器里的一块 UI（issue/activity 选择 + 上传）与对应的持久化/同步逻辑。
/// 编辑器（<c>WorkEditorViewModel</c>）持有一个 region 实例（无 tracker 时为 null），
/// 在保存/同步/克隆/上传等时机回调本接口，generic 编辑器本身不感知具体 tracker。
/// </summary>
public interface ITrackerEditorRegion
{
    /// <summary>是否锁住 generic 编辑字段（如已上传到远程，不可再改）。</summary>
    bool IsLocked { get; }

    /// <summary>当前工作项是否可删除（如未上传才可删）。</summary>
    bool CanDelete { get; }

    /// <summary>
    /// 切换/加载当前工作项的 tracker 绑定。<paramref name="preloadedBinding"/> 为批量预取的
    /// 绑定对象（RedMine 实现里是 <c>WorkTimeEntry</c>），为 null 时由 region 自行从 DB 加载。
    /// </summary>
    void OnWorkItemChanged(WorkItem? item, object? preloadedBinding = null);

    /// <summary>保存时持久化 tracker 绑定到本地 DB（如 CreateWorkTimeEntry）。</summary>
    void OnSave(WorkItem item);

    /// <summary>把当前选择复制到另一个 region（重复工作项用）。</summary>
    void OnCloneTo(ITrackerEditorRegion? target);

    /// <summary>上传到远程，返回（是否成功, 错误信息）。</summary>
    Task<(bool ok, string? error)> UploadAsync(WorkItem item);

    /// <summary>按 id 选中 activity（模板默认值应用用）。</summary>
    void SetActivity(int id);

    /// <summary>按 id 选中 issue（模板默认值应用用）。</summary>
    void SetIssue(int id);
}
