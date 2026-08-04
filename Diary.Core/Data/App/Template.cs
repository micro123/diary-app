namespace Diary.Core.Data.App;

public record Template
{
    public string Name { get; set; } = string.Empty;
    public string DefaultTitle { get; set; } = string.Empty;
    public double DefaultTime { get; set; } = 0;

    // deprecated: 仅保留供旧 templates.json 迁移读取（见 TemplateManager 迁移逻辑）。新代码走 Extensions。
    public int DefaultActivity { get; set; } = 0;
    public int DefaultIssue { get; set; } = 0;

    public ICollection<int> DefaultWorkTags { get; set; } = Array.Empty<int>();

    /// <summary>tracker 扩展透明 payload（文档 §11.2）。核心只保存/保留，不解析 tracker 专属字段。</summary>
    public ICollection<TemplateExtensionData> Extensions { get; set; } = Array.Empty<TemplateExtensionData>();
}
