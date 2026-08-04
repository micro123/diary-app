using Diary.Core.Data.App;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
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
    private readonly TrackerTemplateContributorRegistry _registry;

    public TemplateCoordinator(TrackerTemplateContributorRegistry registry)
    {
        _registry = registry;
    }

    private IReadOnlyList<ITrackerTemplateContributor> Contributors => _registry.Contributors;

    /// <summary>
    /// 为已有扩展和当前已注册 contributor 创建编辑器区。
    /// 找不到 contributor 或 payload 无法解析时跳过编辑控件，保存时保留原始 entry。
    /// </summary>
    public IReadOnlyList<TemplateEditorSlot> LoadEditors(Template template)
    {
        var slots = new List<TemplateEditorSlot>();
        var contributors = Contributors.ToList();
        var entries = template.Extensions
            .GroupBy(entry => (entry.PluginId, entry.InstanceId))
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var contributor in contributors)
        {
            var key = (contributor.PluginId, contributor.InstanceId);
            if (entries.TryGetValue(key, out var entry))
            {
                var data = contributor.Deserialize(entry.PayloadJson, entry.SchemaVersion);
                if (data is null)
                    continue; // 保留损坏或未来版本 payload，避免默认值覆盖原数据

                slots.Add(CreateSlot(contributor, entry, data, template));
                continue;
            }

            var defaultData = contributor.CreateDefaultData();
            var defaultEntry = new TemplateExtensionData
            {
                PluginId = contributor.PluginId,
                InstanceId = contributor.InstanceId,
                SchemaVersion = contributor.CurrentSchemaVersion,
                PayloadJson = contributor.Serialize(defaultData),
            };
            slots.Add(CreateSlot(contributor, defaultEntry, defaultData, template));
        }

        return slots;
    }

    private static TemplateEditorSlot CreateSlot(
        ITrackerTemplateContributor contributor,
        TemplateExtensionData entry,
        object data,
        Template template)
    {
        var editor = contributor.CreateEditor(data, new TemplateEditorContext(template.Name, template.Name));
        return new TemplateEditorSlot
        {
            Editor = editor,
            Contributor = contributor,
            Original = entry,
        };
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

    /// <summary>应用模板扩展到工作项编辑器的各 tracker 扩展（按实例身份匹配）。</summary>
    public void Apply(Template template, ViewModels.WorkEditorViewModel work)
        => Apply(template, work.Extensions);

    /// <summary>可独立测试的模板应用入口。</summary>
    public void Apply(Template template, IEnumerable<ITrackerEditorExtension> extensions)
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
            var key = new TrackerKey(entry.PluginId, entry.InstanceId);
            var ext = extensions.FirstOrDefault(e => e.Key == key);
            if (ext is null)
                continue;
            contributor.ApplyTo(data, ext);
        }
    }
}
