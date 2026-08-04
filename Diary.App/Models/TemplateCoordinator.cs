using Diary.Core.Data.App;
using Diary.GUIBase.ViewModels;
using Diary.PluginUI;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Diary.App.Models;

/// <summary>
/// 模板扩展编辑槽：一个 tracker 扩展的 editor VM + 其 contributor + 原始 payload。
/// TemplateViewModel 持槽列表，UI 绑 TrackerEditors（槽.Editor），保存时 SaveEditors 用槽。
/// </summary>
public sealed record TemplateEditorSlot
{
    public required ViewModelBase Editor { get; init; }
    public required ITrackerTemplateContributor Contributor { get; init; }
    public required TemplateExtensionData Original { get; init; }
}

/// <summary>
/// 模板协调器（文档 §11.5）：统一模板扩展数据的 load/save/apply。
/// 按 PluginId+InstanceId 查找已注册 <see cref="ITrackerTemplateContributor"/> 委托；
/// 找不到 contributor 的 entry 保留原 payload（§11.3 插件未安装时不丢数据）。
/// </summary>
[DiAutoRegister(singleton: true)]
public class TemplateCoordinator
{
    private readonly IServiceProvider _services;

    public TemplateCoordinator(IServiceProvider services)
    {
        _services = services;
    }

    private IEnumerable<ITrackerTemplateContributor> Contributors
        => _services.GetService<IEnumerable<ITrackerTemplateContributor>>() ?? Enumerable.Empty<ITrackerTemplateContributor>();

    /// <summary>为模板的每个 Extensions entry 创建编辑器区。找不到 contributor 的 entry 跳过（不显示编辑控件）。</summary>
    public IReadOnlyList<TemplateEditorSlot> LoadEditors(Template template)
    {
        var slots = new List<TemplateEditorSlot>();
        var contributors = Contributors.ToList();
        foreach (var entry in template.Extensions)
        {
            var contributor = contributors.FirstOrDefault(c =>
                c.PluginId == entry.PluginId && c.InstanceId == entry.InstanceId);
            if (contributor is null)
                continue; // §11.3：插件未安装，不显示编辑控件，payload 保留（SaveEditors 时原样写回）
            var data = contributor.Deserialize(entry.PayloadJson, entry.SchemaVersion) ?? contributor.CreateDefaultData();
            var editor = contributor.CreateEditor(data, new TemplateEditorContext(template.Name, template.Name));
            slots.Add(new TemplateEditorSlot
            {
                Editor = editor,
                Contributor = contributor,
                Original = entry,
            });
        }
        return slots;
    }

    /// <summary>从编辑槽序列化回 payload；找不到 contributor 的原 entry 原样保留。</summary>
    public IReadOnlyList<TemplateExtensionData> SaveEditors(IReadOnlyList<TemplateEditorSlot> slots, Template original)
    {
        var result = new List<TemplateExtensionData>();
        var covered = new HashSet<(string, string)>();
        foreach (var slot in slots)
        {
            var data = slot.Contributor.ExtractData(slot.Editor);
            var payload = slot.Contributor.Serialize(data);
            result.Add(new TemplateExtensionData
            {
                PluginId = slot.Original.PluginId,
                InstanceId = slot.Original.InstanceId,
                SchemaVersion = slot.Contributor.CurrentSchemaVersion,
                PayloadJson = payload,
            });
            covered.Add((slot.Original.PluginId, slot.Original.InstanceId));
        }
        // 保留找不到 contributor 的原 entry（不丢数据）
        foreach (var entry in original.Extensions)
        {
            if (covered.Contains((entry.PluginId, entry.InstanceId)))
                continue;
            result.Add(entry);
        }
        return result;
    }

    /// <summary>应用模板扩展到工作项编辑器的各 tracker 扩展（按 InstanceId 匹配）。</summary>
    public void Apply(Template template, ViewModels.WorkEditorViewModel work)
    {
        var contributors = Contributors.ToList();
        foreach (var entry in template.Extensions)
        {
            var contributor = contributors.FirstOrDefault(c =>
                c.PluginId == entry.PluginId && c.InstanceId == entry.InstanceId);
            if (contributor is null)
                continue;
            var data = contributor.Deserialize(entry.PayloadJson, entry.SchemaVersion);
            if (data is null)
                continue;
            var ext = work.Extensions.FirstOrDefault(e => e.InstanceId == entry.InstanceId);
            if (ext is null)
                continue;
            contributor.ApplyTo(data, ext);
        }
    }
}
