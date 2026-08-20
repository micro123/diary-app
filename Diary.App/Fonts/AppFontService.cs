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
            if (resolved.Collection is not null)
                return ApplyFileFont(application, resolved);

            var fontFamily = string.IsNullOrWhiteSpace(resolved.DefaultFamilyName)
                ? FontManager.Current.DefaultFontFamily
                : new FontFamily(resolved.DefaultFamilyName);
            SetResource(application, fontFamily);
            RemoveUserFontCollection();
            LogApplyResult(fontFamily, resolved.Warning);
            return new AppFontApplyResult(fontFamily, resolved.Warning);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "应用界面字体失败，尝试应用后备字体");
            return ApplyBundledFallback(application, $"应用字体失败：{exception.Message}");
        }
    }

    private AppFontApplyResult ApplyFileFont(Application application, ResolvedAppFont resolved)
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
        LogApplyResult(fontFamily, resolved.Warning);
        return new AppFontApplyResult(fontFamily, resolved.Warning);
    }

    private AppFontApplyResult ApplyBundledFallback(Application application, string reason)
    {
        try
        {
            var resolved = AppFontConfiguration.ResolveBundledFallback(reason);
            if (resolved.Collection is not null)
                return ApplyFileFont(application, resolved);

            return ApplySystemFallback(application, resolved.Warning!);
        }
        catch (Exception exception)
        {
            return ApplySystemFallback(
                application,
                $"{reason} 应用后备字体失败（{exception.Message}），已回退到系统默认字体。");
        }
    }

    private AppFontApplyResult ApplySystemFallback(Application application, string warning)
    {
        var fallback = FontManager.Current.DefaultFontFamily;
        SetResource(application, fallback);
        RemoveUserFontCollection();
        logger.LogWarning("{Warning}", warning);
        return new AppFontApplyResult(fallback, warning);
    }

    private void LogApplyResult(FontFamily fontFamily, string? warning)
    {
        if (string.IsNullOrWhiteSpace(warning))
            logger.LogInformation("界面字体已切换为 {FontFamily}", fontFamily.Name);
        else
            logger.LogWarning("{Warning} 当前字体为 {FontFamily}", warning, fontFamily.Name);
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
