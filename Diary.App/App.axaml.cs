using System;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Diary.App.ViewModels;
using Diary.App.Views;
using Diary.Core.Data.AppConfig;
using Diary.Core.Utils;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Diary.App
{
    public sealed partial class App : Application
    {
        public App()
        {
            Name = "Diary App NG";
            Services = ConfigureServices();
        }

        public override void Initialize()
        {
            LoadConfigurations();
            AvaloniaXamlLoader.Load(this);
            
            // 同步主题设置
            SyncTheme();
        }

        public new static App Current => (Application.Current as App)!;

        public IServiceProvider Services { get; }
        public AllConfig AppConfig => AllConfig.Instance;

        public ILogger Logger => Logging.Logger;

        private IServiceProvider ConfigureServices()
        {
            Logger.LogInformation("Configuring services");
            IServiceCollection services = new ServiceCollection();

            services.AddTypesFromAssembly(Assembly.GetExecutingAssembly());

            // TODO: Add More
            services.AddSingleton(Logging.Logger);

            return services.BuildServiceProvider();
        }

        private void SyncTheme()
        {
            switch (AppConfig.ViewSettings.DefaultColorTheme)
            {
                case "Light": RequestedThemeVariant = ThemeVariant.Light; break;
                case "Dark": RequestedThemeVariant = ThemeVariant.Dark; break;
                case "Auto": RequestedThemeVariant = ThemeVariant.Default; break;
                default: throw new ArgumentOutOfRangeException(nameof(AppConfig.ViewSettings.DefaultColorTheme));
            }
            Logger.LogDebug($"Theme: {ActualThemeVariant}");
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
                // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
                DisableAvaloniaDataAnnotationValidation();
                desktop.MainWindow = new MainWindow();
                var vm = Services.GetRequiredService<MainWindowViewModel>();
                vm.View = desktop.MainWindow;
                desktop.MainWindow.DataContext = vm;
                desktop.ShutdownRequested += (_, _) => SaveConfigurations();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void SaveConfigurations()
        {
            EasySaveLoad.Save(AppConfig);
        }

        private void LoadConfigurations()
        {
            EasySaveLoad.Load(AppConfig);
        }

        private void DisableAvaloniaDataAnnotationValidation()
        {
            // Get an array of plugins to remove
            var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            // remove each entry found
            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }
    }
}