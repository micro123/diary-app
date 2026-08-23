using Avalonia;
using Diary.App.Diagnostics;
using Diary.Core.Constants;
using Diary.GUIBase.Utils;
using Diary.Utils;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.FontAwesome;
using Optris.Icons.Avalonia.MaterialDesign;

namespace Diary.App
{
    internal sealed class Program
    {
        private static int _restartRequested;

        internal static void RequestRestart() => Interlocked.Exchange(ref _restartRequested, 1);

        internal static bool ConsumeRestartRequest()
            => Interlocked.Exchange(ref _restartRequested, 0) == 1;

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            if (CrashReporterProcess.TryRun(args))
                return;
            CrashReporterProcess.InstallUnhandledExceptionCapture();
            App.StartupOptions = AppStartupOptions.Parse(args);
            var appId = AppInfo.AppName;
#if DEBUG
            appId = DebugUiAutomation.ConfigureProcess(appId);
#endif
            var restartRequested = false;
            // 新实例必须等当前实例释放单实例文件锁和命名管道后再启动。
            using (var single = new SingletonApp(appId))
            {
                if (!single.IsSelfInstance())
                {
                    single.Notify("raise");
                    return;
                }
                // 第一个实例：注册唤起回调（RaiseMainWindow 处理器内部已用 Dispatcher.Post 切回 UI 线程）
                single.WakeupAction = _ => EventDispatcher.RunCommand(CommandNames.RaiseMainWindow);

                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                restartRequested = ConsumeRestartRequest();
            }

            if (restartRequested && !ProcUtils.TryStartNewInstance())
                System.Diagnostics.Trace.TraceError("应用已退出，但无法启动重启后的新实例。");
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => BuildAvaloniaApp(null);

        internal static AppBuilder BuildAvaloniaApp(Func<App>? appFactory)
        {
            IconProvider.Current
                .Register<FontAwesomeIconProvider>()
                .Register<MaterialDesignIconProvider>();

            var builder = appFactory is null
                ? AppBuilder.Configure<App>()
                : AppBuilder.Configure(appFactory);
            return builder
                .UsePlatformDetect()
                .LogToTrace();
        }
    }
}
