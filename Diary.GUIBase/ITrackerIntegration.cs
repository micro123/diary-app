using Diary.GUIBase.ViewModels;

namespace Diary.GUIBase;

/// <summary>
/// 一个可插拔的 tracker 集成（RedMine 是第一个实现）。经 DI 注册为 <c>ITrackerIntegration</c>
/// （<c>[DiAutoRegister(singleton:true, serviceType:typeof(ITrackerIntegration))]</c>），
/// <c>IEnumerable&lt;ITrackerIntegration&gt;</c> 可从 DI 解析。编辑器/导航据此扩展，不改核心。
/// </summary>
public interface ITrackerIntegration
{
    /// <summary>稳定标识（如 "RedMine"）。</summary>
    string Key { get; }

    /// <summary>导航页显示名（如 PageNames.RedMineTool）。</summary>
    string DisplayName { get; }

    /// <summary>导航页图标。</summary>
    string Icon { get; }

    /// <summary>是否已配置可用（如 RedMineConfig.Valid()）。gate 运行期行为，不影响注册。</summary>
    bool IsConfigured { get; }

    /// <summary>为工作项编辑器创建一个 tracker 区；tracker 未就绪时返回 null。</summary>
    ITrackerEditorRegion? CreateEditorRegion();

    /// <summary>
    /// 批量加载某日所有工作项的 tracker 绑定（RedMine 实现里是 WorkTimeEntry，装箱为 object）。
    /// 返回 workId→binding；无 tracker 或无数据返回 null。供编辑器列表批量同步，避免逐项查询。
    /// </summary>
    IDictionary<int, object?>? LoadBindingsByDate(string date);

    /// <summary>顶级管理页 VM（如 RedMineManageViewModel）；无管理页返回 null。</summary>
    ViewModelBase? CreateManagePage();
}
