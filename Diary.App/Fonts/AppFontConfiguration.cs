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
    public const string BundledFallbackFontFileName = "NotoSansMonoCJKsc-Regular.otf";

    public static string BundledFallbackFontPath => Path.Combine(
        AppContext.BaseDirectory,
        "Fonts",
        BundledFallbackFontFileName);

    public static ResolvedAppFont Resolve(ViewConfig settings, string? bundledFallbackFontPath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bundledFallbackFontPath ??= BundledFallbackFontPath;

        return settings.FontSource switch
        {
            AppFontSource.BundledDefault => ResolveBundledDefault(bundledFallbackFontPath),
            AppFontSource.SystemDefault => new ResolvedAppFont(null, null, null),
            AppFontSource.SystemFont => ResolveSystemFont(settings.SystemFontFamily, bundledFallbackFontPath),
            AppFontSource.FontFile => ResolveFontFile(settings.FontFilePath, bundledFallbackFontPath),
            _ => ResolveBundledFallback(
                $"未知字体来源“{settings.FontSource}”。",
                bundledFallbackFontPath),
        };
    }

    public static ResolvedAppFont ResolveBundledDefault(string? bundledFallbackFontPath = null)
    {
        bundledFallbackFontPath ??= BundledFallbackFontPath;
        if (TryCreateFont(bundledFallbackFontPath, out var resolved, out var error))
            return resolved;

        return new ResolvedAppFont(
            null,
            null,
            $"应用默认字体不可用（{error}），已回退到系统默认字体。");
    }

    public static ResolvedAppFont ResolveBundledFallback(
        string reason,
        string? bundledFallbackFontPath = null)
    {
        bundledFallbackFontPath ??= BundledFallbackFontPath;
        if (TryCreateFont(bundledFallbackFontPath, out var resolved, out var error))
            return resolved with { Warning = $"{reason} 已回退到应用后备字体。" };

        return new ResolvedAppFont(
            null,
            null,
            $"{reason} 应用后备字体不可用（{error}），已回退到系统默认字体。");
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

    private static ResolvedAppFont ResolveSystemFont(
        string? configuredFamilyName,
        string bundledFallbackFontPath)
    {
        if (string.IsNullOrWhiteSpace(configuredFamilyName))
            return ResolveBundledFallback("未选择系统字体。", bundledFallbackFontPath);

        try
        {
            var familyName = FontManager.Current.SystemFonts
                .Select(family => family.Name)
                .FirstOrDefault(family => string.Equals(family, configuredFamilyName, StringComparison.OrdinalIgnoreCase));
            return familyName is null
                ? ResolveBundledFallback(
                    $"系统字体“{configuredFamilyName}”不可用。",
                    bundledFallbackFontPath)
                : new ResolvedAppFont(familyName, null, null);
        }
        catch (Exception exception)
        {
            return ResolveBundledFallback(
                $"枚举系统字体失败：{exception.Message}",
                bundledFallbackFontPath);
        }
    }

    private static ResolvedAppFont ResolveFontFile(
        string? configuredPath,
        string bundledFallbackFontPath)
    {
        if (TryCreateFont(configuredPath, out var resolved, out var error))
            return resolved;

        return ResolveBundledFallback(error, bundledFallbackFontPath);
    }

    private static bool TryCreateFont(
        string? path,
        out ResolvedAppFont resolved,
        out string error)
    {
        resolved = new ResolvedAppFont(null, null, null);
        if (!TryInspectFontFile(path, out var familyName, out error))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path!);
            var collection = new UserFontCollection(File.ReadAllBytes(fullPath), familyName);
            resolved = new ResolvedAppFont(
                $"{UserFontCollection.CollectionKey.AbsoluteUri}#{familyName}",
                collection,
                null);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"读取字体文件失败：{exception.Message}";
            return false;
        }
    }
}
