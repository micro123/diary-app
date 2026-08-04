namespace Diary.PluginUI;

/// <summary>
/// 模板扩展数据（文档 §11.2）。核心模板只保存透明 payload，不解析 tracker 专属字段。
/// <see cref="PayloadJson"/> 保存稳定业务标识（如 issueId/activityId），不保存 UI 索引。
/// </summary>
public sealed record TemplateExtensionData
{
    public required string PluginId { get; init; }
    public required string InstanceId { get; init; }
    public int SchemaVersion { get; init; }
    public required string PayloadJson { get; init; }
}

/// <summary>模板编辑区域上下文（文档 §11.4 仅提及，本刀为最小占位，行为后续增量补）。</summary>
public sealed record TemplateEditorContext(string TemplateId, string TemplateName);
