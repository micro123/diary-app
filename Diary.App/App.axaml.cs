using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Messages;
using Diary.App.Models;
using Diary.App.Utils;
using Diary.App.ViewModels;
using Diary.App.Views;
using Diary.Core;
using Diary.Core.Constants;
using Diary.Core.Data.AppConfig;
using Diary.Core.Utils;
using Diary.Database;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
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
            EnumerateDbProviders();
            LoadConfigurations();
            AvaloniaXamlLoader.Load(this);

            // 同步主题设置
            SyncTheme();
        }

        private void ConfigureCheck()
        {
            UseDb?.Close();
            UseDb = null;
            
            // 从配置获取当前的数据库提供程序
            var factory = _dbFactories.FirstOrDefault(x => x.Name == AppConfig.DbSettings.DatabaseDriver);
            if (factory == null)
            {
                EventDispatcher.RouteToPage(PageNames.Settings);
                EventDispatcher.Notify("错误", "需要检查数据库设置！");
            }
            else
            {
                UseDb = factory.Create();
                Debug.Assert(UseDb != null);
                if (UseDb.Config != null)
                    EasySaveLoad.Load(UseDb.Config);
                // open
                if (!UseDb.Connect())
                {
                    UseDb = null;
                    EventDispatcher.RouteToPage(PageNames.Settings);
                    EventDispatcher.Notify("错误", "数据库连接失败！");
                    return;
                }

                // init
                if (!UseDb.Initialized())
                {
                    UseDb = null;
                    EventDispatcher.RouteToPage(PageNames.Settings);
                    EventDispatcher.Notify("错误", "数据库初始化失败！");
                    return;
                }
                
                // version check
                if (UseDb.GetDataVersion() != DataVersion.VersionCode)
                {
                    if (!UseDb.UpdateTables(DataVersion.VersionCode))
                    {
                        EventDispatcher.Notify("错误", "数据库升级失败了，可能是程序bug！");
                        return;
                    }
                }
                EventDispatcher.RouteToPage(PageNames.DiaryEditor);
            }
        }

        private List<IDbFactory> _dbFactories = new();
        private void EnumerateDbProviders()
        {
            var dbProviders = TypeLoader.GetImplementations<IDbFactory>(FsTools.GetBinaryDirectory(), "Diary.Db.*.dll");
            foreach (var dbProvider in dbProviders)
            {
                Logger.LogInformation($"Db provider: {dbProvider.Name}");
                _dbFactories.Add(dbProvider);
            }
        }

        public new static App Current => (Application.Current as App)!;

        public IServiceProvider Services { get; }
        public AllConfig AppConfig => AllConfig.Instance;

        public ILogger Logger => Logging.Logger;
        
        public DbInterfaceBase? UseDb { get; private set; }

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
                desktop.ShutdownRequested += (_, _) => PreShutdown();
            }

            base.OnFrameworkInitializationCompleted();
            
            WeakReferenceMessenger.Default.Register<ConfigUpdateEvent>(this, (r, m) =>
            {
                Dispatcher.UIThread.Post(ConfigureCheck);
            });
            // check if configure is valid
            ConfigureCheck();
            
            // start keep-alive thread
            StartKeepAliveTimer();
        }

        private void PreShutdown()
        {
            _timer.Stop();
            SaveConfigurations();
        }

        private readonly DispatcherTimer _timer = new();
        private void StartKeepAliveTimer()
        {
            _timer.Interval = TimeSpan.FromSeconds(30);
            _timer.Tick += (_, _) =>
            {
                Logger.LogDebug($"DB keep alive...");
                UseDb?.KeepAlive();
            };
            _timer.Start();
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

        public SettingItemModel CreateFor(string caption, string key, object obj, PropertyInfo property)
        {
            return key switch
            {
                "DB_DRIVER" => new SettingChoice(caption, _dbFactories.Select(x=>x.Name), obj, property),
                _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
            };
        }
    }
}