using System;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Diary.App.ViewModels;
using Diary.App.Views;
using Diary.Core.Data.AppConfig;
using Diary.Core.Utils;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        }
        
        public new static App Current => (Application.Current as App)!;
        
        public IServiceProvider Services { get; }
        public AllConfig AppConfig => AllConfig.Instance;

        private static IServiceProvider ConfigureServices()
        {
            IServiceCollection services = new ServiceCollection();
            
            services.AddTypesFromAssembly(Assembly.GetExecutingAssembly());
            
            // TODO: Add More
            
            return services.BuildServiceProvider();
        }

        
        
        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
                // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
                DisableAvaloniaDataAnnotationValidation();
                desktop.MainWindow = new MainWindow
                {
                    DataContext = Services.GetRequiredService<MainWindowViewModel>(),
                };
                desktop.ShutdownRequested += (_, _) => SaveConfigurations();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void SaveConfigurations()
        {
            EasySaveLoad.Save(AllConfig.Instance);
        }

        private void LoadConfigurations()
        {
            EasySaveLoad.Load(AllConfig.Instance);
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