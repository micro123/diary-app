using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Diary.Core.Data.AppConfig;
using Diary.Utils;
using Microsoft.Extensions.Logging;

namespace Diary.App.Fonts;

public sealed record AppFontApplyResult(FontFamily FontFamily, string? Warning)
{
    public bool UsedFallback => !string.IsNullOrWhiteSpace(Warning);
}

[DiAutoRegister(singleton: true)]
public sealed class AppFontService(ILogger<AppFontService> logger)
{
    public const string ResourceKey = "AppFontFamily";

    public AppFontApplyResult Apply(Application application, ViewConfig settings)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(settings);
        Dispatcher.UIThread.VerifyAccess();

        try
        {
            var resolved = AppFontConfiguration.Resolve(settings);
            if (!string.IsNullOrWhiteSpace(resolved.Warning))
                return ApplyFallback(application, resolved.Warning);

            if (resolved.Collection is not null)
                return ApplyUserFont(application, resolved);

            var fontFamily = string.IsNullOrWhiteSpace(resolved.DefaultFamilyName)
                ? FontManager.Current.DefaultFontFamily
                : new FontFamily(resolved.DefaultFamilyName);
            SetResource(application, fontFamily);
            RemoveUserFontCollection();
            logger.LogInformation("界面字体已切换为 {FontFamily}", fontFamily.Name);
            return new AppFontApplyResult(fontFamily, null);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "应用界面字体失败，回退到系统默认字体");
            return ApplyFallback(
                application,
                $"应用字体失败，已回退到系统默认字体：{exception.Message}");
        }
    }

    private AppFontApplyResult ApplyUserFont(Application application, ResolvedAppFont resolved)
    {
        var collection = resolved.Collection!;
        try
        {
            // 先解除界面对旧集合的动态资源引用，再用同一集合键替换字体数据。
            SetResource(application, FontManager.Current.DefaultFontFamily);
            FontManager.Current.AddFontCollection(collection);
        }
        catch
        {
            collection.Dispose();
            throw;
        }

        var fontFamily = new FontFamily(resolved.DefaultFamilyName!);
        SetResource(application, fontFamily);
        logger.LogInformation("界面字体已从外部文件切换为 {FontFamily}", fontFamily.Name);
        return new AppFontApplyResult(fontFamily, null);
    }

    private AppFontApplyResult ApplyFallback(Application application, string warning)
    {
        var fallback = FontManager.Current.DefaultFontFamily;
        SetResource(application, fallback);
        RemoveUserFontCollection();
        logger.LogWarning("{Warning}", warning);
        return new AppFontApplyResult(fallback, warning);
    }

    private static void SetResource(Application application, FontFamily fontFamily)
    {
        application.Resources[ResourceKey] = fontFamily;
    }

    private static void RemoveUserFontCollection()
    {
        try
        {
            FontManager.Current.RemoveFontCollection(UserFontCollection.CollectionKey);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"移除运行时字体集合失败，将在进程退出时释放：{exception.Message}");
        }
    }
}
