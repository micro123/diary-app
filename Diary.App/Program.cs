using System;
using Avalonia;
using Avalonia.Logging;
using Avalonia.Media;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
using Projektanker.Icons.Avalonia.MaterialDesign;

namespace Diary.App
{
    internal sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args) => BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
        {
            IconProvider.Current
                .Register<FontAwesomeIconProvider>()
                .Register<MaterialDesignIconProvider>();
            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // .WithInterFont()
                .With(new FontManagerOptions
                {
                    DefaultFamilyName = "avares://Diary.App/Assets/Fonts#Noto Sans",
                    FontFallbacks =
                    [
                        new FontFallback { FontFamily = "avares://Diary.App/Assets/Fonts#OpenMoji", }
                    ]
                })
                .LogToTrace();
        }
    }
}
