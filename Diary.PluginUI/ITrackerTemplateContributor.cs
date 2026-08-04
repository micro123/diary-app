using Diary.GUIBase.ViewModels;
using Diary.PluginBase;

namespace Diary.PluginUI;

/// <summary>
/// tracker 模板贡献者（文档 §11.4）。插件负责其模板扩展数据的创建、编辑、序列化、
/// 反序列化、应用到编辑器扩展。核心模板协调器按 PluginId+InstanceId 查找已启用插件并委托。
/// </summary>
public interface ITrackerTemplateContributor
{
    string PluginId { get; }
    string InstanceId { get; }
    int CurrentSchemaVersion { get; }

    object CreateDefaultData();
    ViewModelBase CreateEditor(object? data, TemplateEditorContext context);
    /// <summary>从编辑器 VM 抽取当前数据（contributor 知道自己的 editor 类型，转型取数据）。</summary>
    object ExtractData(ViewModelBase editor);
    string Serialize(object data);
    object? Deserialize(string payloadJson, int schemaVersion);
    void ApplyTo(object data, ITrackerEditorExtension target);
}

public interface ITrackerTemplateContributorFactory
{
    string PluginId { get; }
    ITrackerTemplateContributor Create(ITrackerInstance instance);
}

/// <summary>模板编辑区域上下文（文档 §11.4 仅提及，本刀为最小占位，行为后续增量补）。</summary>
public sealed record TemplateEditorContext(string TemplateId, string TemplateName);
