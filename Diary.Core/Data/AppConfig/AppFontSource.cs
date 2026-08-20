namespace Diary.Core.Data.AppConfig;

public static class AppFontSource
{
    public const string SystemDefault = "跟随系统";
    public const string SystemFont = "系统字体";
    public const string FontFile = "字体文件";

    public static IReadOnlyList<string> Options { get; } =
        [SystemDefault, SystemFont, FontFile];
}
