using Avalonia.Media;
using Avalonia.Media.Fonts;
using Diary.Core.Data.AppConfig;
using SkiaSharp;

namespace Diary.App.Fonts;

internal sealed record ResolvedAppFont(
    string? DefaultFamilyName,
    IFontCollection? Collection,
    string? Warning);

internal static class AppFontConfiguration
{
    public static ResolvedAppFont Resolve(ViewConfig settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.FontSource switch
        {
            AppFontSource.SystemDefault => new ResolvedAppFont(null, null, null),
            AppFontSource.SystemFont => ResolveSystemFont(settings.SystemFontFamily),
            AppFontSource.FontFile => ResolveFontFile(settings.FontFilePath),
            _ => new ResolvedAppFont(
                null,
                null,
                $"未知字体来源“{settings.FontSource}”，已回退到系统默认字体。"),
        };
    }

    public static bool TryInspectFontFile(
        string? path,
        out string familyName,
        out string error)
    {
        familyName = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "请选择字体文件。";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "字体文件路径无效。";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = "字体文件不存在。";
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
        {
            error = "仅支持 .ttf 和 .otf 字体文件。";
            return false;
        }

        try
        {
            using var typeface = SKTypeface.FromFile(fullPath);
            if (typeface is null || typeface.GlyphCount <= 0 || string.IsNullOrWhiteSpace(typeface.FamilyName))
            {
                error = "无法识别字体文件。";
                return false;
            }

            familyName = typeface.FamilyName.Trim();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = $"读取字体文件失败：{exception.Message}";
            return false;
        }
    }

    private static ResolvedAppFont ResolveSystemFont(string? configuredFamilyName)
    {
        if (string.IsNullOrWhiteSpace(configuredFamilyName))
        {
            return new ResolvedAppFont(
                null,
                null,
                "未选择系统字体，已回退到系统默认字体。");
        }

        try
        {
            var familyName = FontManager.Current.SystemFonts
                .Select(family => family.Name)
                .FirstOrDefault(family => string.Equals(family, configuredFamilyName, StringComparison.OrdinalIgnoreCase));
            return familyName is null
                ? new ResolvedAppFont(
                    null,
                    null,
                    $"系统字体“{configuredFamilyName}”不可用，已回退到系统默认字体。")
                : new ResolvedAppFont(familyName, null, null);
        }
        catch (Exception exception)
        {
            return new ResolvedAppFont(
                null,
                null,
                $"枚举系统字体失败，已回退到系统默认字体：{exception.Message}");
        }
    }

    private static ResolvedAppFont ResolveFontFile(string? configuredPath)
    {
        if (!TryInspectFontFile(configuredPath, out var familyName, out var error))
            return new ResolvedAppFont(null, null, $"{error} 已回退到系统默认字体。");

        try
        {
            var fullPath = Path.GetFullPath(configuredPath!);
            var collection = new UserFontCollection(File.ReadAllBytes(fullPath), familyName);
            return new ResolvedAppFont(
                $"{UserFontCollection.CollectionKey.AbsoluteUri}#{familyName}",
                collection,
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ResolvedAppFont(
                null,
                null,
                $"读取字体文件失败，已回退到系统默认字体：{exception.Message}");
        }
    }
}
