using Avalonia;
using Avalonia.Media;
using Diary.Core.Constants;
using Diary.GUIBase.Utils;
using Diary.Utils;
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
        public static void Main(string[] args)
        {
            // 跨平台单实例守卫：已有实例在跑则唤起它后退出，不重复启动
            using var single = new SingletonApp(AppInfo.AppName);
            if (!single.IsSelfInstance())
            {
                single.Notify("raise");
                return;
            }
            // 第一个实例：注册唤起回调（RaiseMainWindow 处理器内部已用 Dispatcher.Post 切回 UI 线程）
            single.WakeupAction = _ => EventDispatcher.RunCommand(CommandNames.RaiseMainWindow);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
        {
            IconProvider.Current
                .Register<FontAwesomeIconProvider>()
                .Register<MaterialDesignIconProvider>();

            return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new FontManagerOptions
                {
                    DefaultFamilyName = "avares://Diary.App/Assets/Fonts#LXGW WenKai Mono",
                    FontFallbacks =
                    [
                        new FontFallback { FontFamily = "avares://Diary.App/Assets/Fonts#OpenMoji", }
                    ]
                })
                .LogToTrace();
        }
    }
}
