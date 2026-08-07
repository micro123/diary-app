using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Diary.App;
using Diary.App.Models;
using Diary.App.ViewModels;
using Diary.App.Views;
using Diary.Database;
using Diary.Db.SQLite;
using Diary.PluginBase;
using Microsoft.Extensions.DependencyInjection;
using DiaryApplication = Diary.App.App;

namespace Diary.DbTests;

[TestClass]
[DoNotParallelize]
public sealed class CoreOnlyStartupAcceptanceTests
{
    [TestMethod]
    public void HeadlessDesktopStartup_BuildsCoreDatabaseAndMainWindowWithoutTrackers()
    {
        DiaryApplication.StartupOptions = AppStartupOptions.Parse([AppStartupOptions.CoreOnlyArgument]);
        var factory = new InMemorySQLiteFactory();
        DiaryApplication? app = null;
        IClassicDesktopStyleApplicationLifetime? lifetime = null;

        try
        {
            Program.BuildAvaloniaApp(() => new DiaryApplication([factory]))
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithClassicDesktopLifetime(
                    [AppStartupOptions.CoreOnlyArgument],
                    desktop =>
                    {
                        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        lifetime = desktop;
                    });

            app = Assert.IsInstanceOfType<DiaryApplication>(Application.Current);
            var mainWindow = Assert.IsInstanceOfType<MainWindow>(lifetime?.MainWindow);
            var mainViewModel = Assert.IsInstanceOfType<MainWindowViewModel>(mainWindow.DataContext);
            var database = Assert.IsInstanceOfType<SQLiteDb>(app.UseDb);
            var extensionHost = (IDbExtensionHost)database;

            Assert.IsTrue(app.DatabaseOk);
            Assert.AreSame(factory, app.UseFactory);
            Assert.IsTrue(extensionHost.Exists(
                "SELECT name FROM sqlite_master WHERE type='table' AND name='work_items';"));
            Assert.AreSame(mainViewModel, app.Services.GetRequiredService<MainWindowViewModel>());
            Assert.IsNotNull(app.Services.GetRequiredService<DbShareData>());
            Assert.IsEmpty(app.Plugins);
            Assert.IsEmpty(app.Services.GetRequiredService<PluginInstanceRegistry>().AllEntries);
            Assert.IsEmpty(app.Services.GetRequiredService<TrackerUiContributionRegistry>().Contributions);
            Assert.IsEmpty(app.Services.GetRequiredService<TrackerPluginDiagnosticsService>().GetSnapshot());
        }
        finally
        {
            lifetime?.MainWindow?.Close();
            app?.UseDb?.Dispose();
            (app?.Services as IDisposable)?.Dispose();
            DiaryApplication.StartupOptions = AppStartupOptions.Default;
        }
    }

    private sealed class InMemorySQLiteFactory : IDbFactory
    {
        private readonly Config _config = new() { FilePath = ":memory:" };

        public string Name => "SQLite";
        public bool Usable => true;
        public DbInterfaceBase Create() => new SQLiteDb(this);
        public Migration? GetMigration(uint version) => null;
        public object GetConfig() => _config;
    }
}
