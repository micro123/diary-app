namespace Diary.Core.Data.App;

/// <summary>
/// 模板扩展数据（文档 §11.2）。核心模板只保存透明 payload，不解析 tracker 专属字段。
/// <see cref="PayloadJson"/> 保存稳定业务标识（如 issueId/activityId），不保存 UI 索引。
/// 位于 Core 以便 <c>Template</c> 持久化引用；不依赖任何 tracker 类型。
/// </summary>
public sealed record TemplateExtensionData
{
    public required string PluginId { get; init; }
    public required string InstanceId { get; init; }
    public int SchemaVersion { get; init; }
    public required string PayloadJson { get; init; }
}
