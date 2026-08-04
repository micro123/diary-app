using System.Diagnostics;
using System.Reflection;
using System.Data.Common;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Diary.App.Models;
using Diary.App.ViewModels;
using Diary.App.Views;
using Diary.Core;
using Diary.Core.Constants;
using Diary.Core.Data.AppConfig;
using Diary.Core.Utils;
using Diary.Database;
using Diary.GUIBase;
using Diary.GUIBase.Events;
using Diary.GUIBase.Utils;
using Diary.GUIBase.ViewModels;
using Diary.PluginBase;
using Diary.RedMine;
using Diary.Survey;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Diary.App
{
    public sealed partial class App : BaseApp
    {
        public App()
        {
            Name = AppInfo.AppName;
            Services = ConfigureServices();

            _surveyor.ReceiveMessage += (_, s) =>
            {
                // 接收事件来自后台线程，转回 UI 线程再分发消息，避免 handler 跨线程操作绑定
                Dispatcher.UIThread.Post(() => EventDispatcher.Msg(new RespondEvent(s)));
            };
            _respondent.ReceiveMessage += (_, s) =>
            {
                Dispatcher.UIThread.Post(() => EventDispatcher.Msg(new SurveyRequestEvent(s)));
            };
        }

        public override void Initialize()
        {
            EnumerateDbProviders();
            LoadConfigurations();

            AvaloniaXamlLoader.Load(this);
            DataContext = Services.GetRequiredService<AppModel>();

            // 同步主题设置
            SyncTheme();
            UpdateSurveyObjects();
        }

        private bool ConfigureCheck(out string message)
        {
            message = string.Empty;
            // do not change existing database
            // if (UseDb != null)
            //     return true;
            UseDb?.Close();
            UseDb = null;

            // 从配置获取当前的数据库提供程序
            UseFactory = _dbFactories.FirstOrDefault(x => x.Name == AppConfig.DbSettings.DatabaseDriver);
            if (UseFactory == null)
            {
                message = $"数据库{AppConfig.DbSettings.DatabaseDriver}不支持，请检查设置";
                return false;
            }

            // 创建数据库
            UseDb = UseFactory.Create();
            Debug.Assert(UseDb != null);
            var dbConfig = UseFactory.GetConfig();
            EasySaveLoad.Load(dbConfig); // 加载数据库配置

            // open
            if (!UseDb.Connect())
            {
                UseDb = null;
                message = "数据库连接失败！";
                return false;
            }

            // init
            if (!UseDb.Initialized())
            {
                UseDb = null;
                message = "数据库初始化失败！";
                return false;
            }

            // version check
            if (UseDb.GetDataVersion() != DataVersion.VersionCode)
            {
                if (!UseDb.UpdateTables(DataVersion.VersionCode))
                {
                    message = "数据库升级失败了，可能是程序bug！";
                    return false;
                }
            }

            Services.GetRequiredService<DbShareData>().InitLoad();
            DatabaseOk = true;

            return true;
        }

        private readonly List<IDbFactory> _dbFactories = new();
        private void EnumerateDbProviders()
        {
            var dbProviders = TypeLoader.GetImplementations<IDbFactory>(FsTools.GetBinaryDirectory(), "Diary.Db.*.dll");
            foreach (var dbProvider in dbProviders)
            {
                Logger.LogInformation("Db provider: {Name}, Usable? {Usable}", dbProvider.Name, dbProvider.Usable);
                if (dbProvider.Usable)
                    _dbFactories.Add(dbProvider);
            }
        }

        public override IServiceProvider Services { get; protected set; }
        public override AllConfig AppConfig => AllConfig.Instance;

        public ILogger Logger => Logging.Logger;

        public override IDbFactory? UseFactory { get; protected set; }
        public override DbInterfaceBase? UseDb { get; protected set; }

        private IServiceProvider ConfigureServices()
        {
            Logger.LogDebug("Configuring services");
            IServiceCollection services = new ServiceCollection();

            // mask add before
            services.AddSingleton(Logging.Logger);
            services.AddSingleton<BaseApp>(this);
            var compatibility = new PluginCompatibilityContext(
                1,
                1,
                DataVersion.VersionCode,
                new HashSet<string>
                {
                    PluginCapabilities.SqlTransactions,
                    PluginCapabilities.ForeignKeys,
                    PluginCapabilities.MultipleStatementExecution,
                });
            var plugins = TypeLoader.GetImplementations<ITrackerPlugin>(
                FsTools.GetBinaryDirectory(), "Diary.RedMine.dll");
            foreach (var plugin in plugins)
            {
                var result = PluginHost.Register(plugin, compatibility, services);
                Logger.LogInformation("Plugin {PluginId}: {State}", plugin.Manifest.Id, result.State);
                if (result.State == PluginState.Blocked)
                    Logger.LogError("Plugin {PluginId} blocked: {Error}", plugin.Manifest.Id, result.Error);
            }
            services.AddTypesFromAssembly(Assembly.GetExecutingAssembly());
            services.AddTypesFromAssembly(typeof(ViewLocator).Assembly);
            services.AddTypesFromAssembly(typeof(Diary.RedMine.UI.IRedMineUiData).Assembly);

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

            Logger.LogDebug("Theme: {Variant}", ActualThemeVariant);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            InstallGlobalExceptionHandlers();

            bool success;
            string message;
            try
            {
                success = ConfigureCheck(out message);
            }
            catch (Exception ex)
            {
                // ConfigureCheck 内部多数路径已 catch，但 GetDataVersion/InitLoad 等仍可能抛
                success = false;
                message = "数据库打开异常";
                Logger.LogError(ex, "ConfigureCheck 抛出未处理异常");
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
                // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
                DisableAvaloniaDataAnnotationValidation();
                desktop.MainWindow = new MainWindow();
                var vm = Services.GetRequiredService<MainWindowViewModel>();
                vm.SetView(desktop.MainWindow);
                desktop.MainWindow.DataContext = vm;
                desktop.ShutdownRequested += (_, _) => PreShutdown();
            }

            base.OnFrameworkInitializationCompleted();

            WeakReferenceMessenger.Default.Register<ConfigUpdateEvent>(this, (r, m) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ConfigureCheck(out var msg))
                    {
                        EventDispatcher.RouteToPage(PageNames.Settings);
                        EventDispatcher.Notify("错误", msg);
                    }
                    SurveyEnabled = AppConfig.SurveySettings.IsServerEnabled;
                });

                UpdateSurveyObjects();
            });
            WeakReferenceMessenger.Default.Register<SurveyResultEvent>(this, (r, m) =>
            {
                _respondent.Send(m.Value);
            });
            WeakReferenceMessenger.Default.Register<SurveyQueryEvent>(this, (r, m) =>
            {
                _surveyor.Survey(m.Value);
            });

            // check if configure is valid
            if (!success)
            {
                EventDispatcher.RouteToPage(PageNames.Settings);
                EventDispatcher.Notify("错误", message);
            }

            // start keep-alive thread
            StartKeepAliveTimer();
        }

        private void UpdateSurveyObjects()
        {
            if (Design.IsDesignMode)
                return;

            _surveyor.StopServer();
            _respondent.Shutdown();

            if (!AppConfig.SurveySettings.Enabled)
                return;

            if (AppConfig.SurveySettings.IsServerEnabled)
            {
                _surveyor.StartServer();
            }

            if (!string.IsNullOrWhiteSpace(AppConfig.SurveySettings.ServerAddress))
            {
                _respondent.Connect(AppConfig.SurveySettings.ServerAddress);
            }
        }

        private void PreShutdown()
        {
            _surveyor.StopServer();
            _respondent.Shutdown();
            _timer.Stop();
            SaveConfigurations();
            (Services as IDisposable)?.Dispose();
            Logging.Shutdown();
        }

        private readonly DispatcherTimer _timer = new();

        /// <summary>
        /// 全局未处理异常兜底：网络断连等导致 DB 调用抛 NpgsqlException 时，避免直接崩溃。
        /// 记日志 + 给提示；连上后下次操作由连接池自愈。
        /// </summary>
        private void InstallGlobalExceptionHandlers()
        {
            // UI 线程未处理异常（DispatcherTimer、绑定命令、async void 等均在 UI 线程分发）
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                var ex = e.Exception;
                Logger.LogError(ex, "未处理的 UI 线程异常");
                var msg = ex is DbException
                    ? "数据库连接异常，请检查网络或数据库设置"
                    : $"未处理异常：{ex.GetType().Name}";
                try { EventDispatcher.Notify("发生错误", msg); }
                catch { /* handler 自身不得再抛 */ }
            };
            // 后台 Task 未观察异常：.NET Core 默认不崩，仍记日志
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Logger.LogError(e.Exception, "未观察的后台任务异常");
                e.SetObserved();
            };
            // AppDomain 兜底：仅记日志（IsTerminating 时无法阻止退出）
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                    Logger.LogError(ex, "AppDomain 未处理异常 IsTerminating={Terminating}", e.IsTerminating);
            };
        }

        private void StartKeepAliveTimer()
        {
            _timer.Interval = TimeSpan.FromSeconds(30);
            _timer.Tick += (_, _) =>
            {
                Logger.LogDebug("DB keep alive...");
                try { UseDb?.KeepAlive(); }
                catch (Exception ex) { Logger.LogWarning(ex, "KeepAlive 失败（可能网络中断）"); }
            };
            _timer.Start();
        }

        private void SaveConfigurations()
        {
            EasySaveLoad.Save(AppConfig);
        }

        private void LoadConfigurations()
        {
            // EasySaveLoad.Load(AppConfig); // already loaded by instance
            SurveyEnabled = AppConfig.SurveySettings.IsServerEnabled;
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

        public override SettingItemModel CreateModelFor(string caption, string helpTip, string key, object obj, PropertyInfo property)
        {
            return key switch
            {
                "DB_DRIVER" => new SettingChoice(caption, helpTip, _dbFactories.Select(x => x.Name), obj, property),
                _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
            };
        }

        private readonly Dictionary<string, RelayCommand> _settingCommands = new();

        public override ICommand? ResolveCommand(string name)
        {
            if (!_settingCommands.TryGetValue(name, out var cmd))
            {
                var mainVm = Services.GetRequiredService<MainWindowViewModel>();
                cmd = new RelayCommand(() => mainVm.ExecuteSettingCommand(name));
                _settingCommands[name] = cmd;
            }
            return cmd;
        }

        private static readonly StyledProperty<bool> DatabaseOkProperty = AvaloniaProperty.Register<App, bool>(nameof(DatabaseOk), false);
        public override bool DatabaseOk
        {
            get => GetValue(DatabaseOkProperty);
            protected set => SetValue(DatabaseOkProperty, value);
        }

        private static readonly StyledProperty<bool> SurveyEnabledProperty = AvaloniaProperty.Register<App, bool>(nameof(SurveyEnabled), false);
        public override bool SurveyEnabled
        {
            get => GetValue(SurveyEnabledProperty);
            protected set => SetValue(SurveyEnabledProperty, value);
        }

        private AppSurveyor _surveyor = new();
        private AppRespondent _respondent = new();
    }
}
