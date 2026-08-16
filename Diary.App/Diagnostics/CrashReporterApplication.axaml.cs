using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Diary.App.Diagnostics;

internal sealed partial class CrashReporterApplication : Application
{
    private readonly CrashReportResult _result;

    public CrashReporterApplication(CrashReportResult result)
    {
        _result = result;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new CrashReporterWindow(_result);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
