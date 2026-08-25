namespace Diary.AiContext;

public sealed class AiContextSectionNotDisclosedException : InvalidOperationException
{
    public AiContextSectionNotDisclosedException(string section)
        : base($"当前快照未授权 {section} 数据节。")
    {
        Section = section;
    }

    public string Section { get; }
}
