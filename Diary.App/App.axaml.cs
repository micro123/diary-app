using System.Diagnostics;
using System.Reflection;
using System.Data.Common;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
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
using Diary.Script.CSharp;
using Diary.Script.Lua;
using Diary.Script.Py;
using Diary.Script.Runtime;
using Diary.ScriptBase;
using Diary.ScriptHost;
using Diary.Survey;
using Diary.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Diary.App
{
    public sealed partial class App : BaseApp
    {
        public static AppStartupOptions StartupOptions { get; set; } = AppStartupOptions.Default;
        private readonly IReadOnlyList<IDbFactory>? _startupDbFactories;

        public App() : this(null)
        {
        }

        internal App(IEnumerable<IDbFactory>? startupDbFactories)
        {
            _startupDbFactories = startupDbFactories?.ToArray();
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
            Services.GetRequiredService<TrackerPluginDiagnosticsService>().SetPluginStates(
                _pluginLoadDiagnostics.Values);

            _ = LoadScriptsAsync();
            AvaloniaXamlLoader.Load(this);
            DataContext = Services.GetRequiredService<AppModel>();

            // 同步主题设置
            SyncTheme();
            UpdateSurveyObjects();
        }

        private async Task LoadScriptsAsync()
        {
            try
            {
                var root = Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts");
                var result = await Services.GetRequiredService<ScriptDirectoryLoadState>()
                    .EnsureLoadedAsync(root);
                Logger.LogInformation(
                    "脚本目录加载完成：发现 {Count} 个脚本，诊断 {DiagnosticCount} 条",
                    result.Entries.Length,
                    result.Diagnostics.Length);
                foreach (var diagnostic in result.Diagnostics)
                {
                    Logger.LogWarning(
                        "脚本诊断 {Code}：{Message}（{SourcePath}）",
                        diagnostic.Code,
                        diagnostic.Message,
                        diagnostic.SourcePath);
                }
                Services.GetRequiredService<ScriptStartupDiagnosticsStore>()
                    .Replace(result.Diagnostics.Select(FormatScriptDiagnostic));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "脚本目录加载失败");
                Services.GetRequiredService<ScriptStartupDiagnosticsStore>().Replace([
                    new ScriptDiagnosticListItem(
                        "错误",
                        "SCRIPT_DIRECTORY_LOAD_FAILED",
                        "脚本目录加载失败，请查看日志或重试。",
                        string.Empty)
                ], loadFailed: true);
            }
        }

        private static ScriptDiagnosticListItem FormatScriptDiagnostic(ScriptDiagnostic diagnostic) =>
            new(
                diagnostic.Severity switch
                {
                    ScriptDiagnosticSeverity.Error => "错误",
                    ScriptDiagnosticSeverity.Warning => "警告",
                    _ => "信息",
                },
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.SourcePath is null ? string.Empty : $"{diagnostic.SourcePath}:{diagnostic.Line}:{diagnostic.Column}");

        private bool ConfigureCheck(out string message)
        {
            message = string.Empty;
            Logger.LogDebug("数据库检查开始：配置驱动 {ConfiguredDriver}，当前连接驱动 {ConnectedDriver}，数据库已连接 {DatabaseOk}",
                AppConfig.DbSettings.DatabaseDriver, _connectedDriver ?? "<none>", DatabaseOk);
            // Tracker 配置更新不应关闭正在使用的数据库连接；插件重注册会复用该连接。
            if (UseDb is not null && DatabaseOk)
            {
                if (string.Equals(_connectedDriver, AppConfig.DbSettings.DatabaseDriver, StringComparison.Ordinal))
                {
                    Logger.LogDebug("数据库检查跳过：继续使用 {Driver}", _connectedDriver);
                    return true;
                }
                Logger.LogInformation("检测到数据库驱动变化：{OldDriver} -> {NewDriver}",
                    _connectedDriver ?? "<none>", AppConfig.DbSettings.DatabaseDriver);
                return ReconfigureDatabase(out message);
            }

            if (!TryConnectDatabase(out message, out var database))
                return false;

            UseDb = database;
            DatabaseOk = true;
            Services.GetRequiredService<DbShareData>().InitLoad();
            return true;
        }

        private bool TryConnectDatabase(out string message, out DbInterfaceBase? database)
        {
            database = null;
            message = string.Empty;

            // 从配置获取当前的数据库提供程序
            var factory = _dbFactories.FirstOrDefault(x => x.Name == AppConfig.DbSettings.DatabaseDriver);
            if (factory == null)
            {
                message = $"数据库{AppConfig.DbSettings.DatabaseDriver}不支持，请检查设置";
                Logger.LogWarning("数据库驱动不可用：{Driver}", AppConfig.DbSettings.DatabaseDriver);
                return false;
            }

            // 创建数据库
            Logger.LogDebug("创建数据库实例：工厂 {Factory}，类型 {DatabaseType}",
                factory.Name, factory.GetType().FullName);
            database = factory.Create();
            Debug.Assert(database != null);
            var dbConfig = factory.GetConfig();
            EasySaveLoad.Load(dbConfig); // 加载数据库配置
            Logger.LogDebug("数据库配置已加载：驱动 {Driver}，配置类型 {ConfigType}",
                factory.Name, dbConfig.GetType().FullName);

            // open
            Logger.LogInformation("开始连接数据库：驱动 {Driver}", factory.Name);
            if (!database.Connect())
            {
                database.Dispose();
                database = null;
                message = "数据库连接失败！";
                Logger.LogWarning("数据库连接失败：驱动 {Driver}", factory.Name);
                return false;
            }
            Logger.LogInformation("数据库连接成功：驱动 {Driver}", factory.Name);

            // init
            if (!database.Initialized())
            {
                database.Dispose();
                database = null;
                message = "数据库初始化失败！";
                Logger.LogWarning("数据库初始化失败：驱动 {Driver}", factory.Name);
                return false;
            }
            Logger.LogDebug("数据库结构初始化成功：驱动 {Driver}", factory.Name);

            // version check
            if (database.GetDataVersion() != DataVersion.VersionCode)
            {
                if (!database.UpdateTables(DataVersion.VersionCode))
                {
                    database.Dispose();
                    database = null;
                    message = "数据库升级失败了，可能是程序bug！";
                    Logger.LogWarning("数据库迁移失败：驱动 {Driver}，目标版本 {Version}",
                        factory.Name, DataVersion.VersionCode);
                    return false;
                }
                Logger.LogDebug("数据库迁移成功：驱动 {Driver}，目标版本 {Version}",
                    factory.Name, DataVersion.VersionCode);
            }
            UseFactory = factory;
            _connectedDriver = factory.Name;
            Logger.LogInformation("数据库连接候选已验证：驱动 {Driver}", _connectedDriver);
            return true;
        }

        public bool ReconfigureDatabase(out string message)
        {
            var oldDriver = AppConfig.DbSettings.DatabaseDriver;
            var oldDatabase = UseDb;
            Logger.LogInformation("开始切换数据库：当前驱动 {OldDriver}，目标驱动 {NewDriver}",
                _connectedDriver ?? "<none>", oldDriver);
            if (!TryConnectDatabase(out message, out var newDatabase))
            {
                AppConfig.DbSettings.DatabaseDriver = UseFactory?.Name ?? oldDriver;
                Logger.LogWarning("数据库切换失败，保留旧连接：驱动恢复为 {Driver}，原因 {Reason}",
                    AppConfig.DbSettings.DatabaseDriver, message);
                return false;
            }

            UseDb = newDatabase;
            DatabaseOk = true;
            oldDatabase?.Close();
            Services.GetRequiredService<DbShareData>().InitLoad();
            RegisterTrackerInstances();
            Logger.LogInformation("数据库切换完成：当前驱动 {Driver}", _connectedDriver);
            return true;
        }

        private readonly List<IDbFactory> _dbFactories = new();
        private string? _connectedDriver;
        private readonly List<ITrackerPlugin> _plugins = new();
        private readonly Dictionary<string, object> _pluginConfigurations = new();
        private readonly Dictionary<string, TrackerPluginLoadDiagnostic> _pluginLoadDiagnostics = new();
        private readonly PluginConfigurationLoader _pluginConfigurationLoader = new();

        private void RegisterTrackerInstances()
        {
            if (UseDb is null)
            {
                Logger.LogWarning("Tracker instance registration skipped: database unavailable");
                return;
            }

            Services.GetRequiredService<TrackerPluginDiagnosticsService>().SetPluginStates(
                _pluginLoadDiagnostics.Values);
            Services.GetRequiredService<TrackerPluginLifecycleCoordinator>().Register(
                UseDb,
                _plugins,
                _pluginConfigurations);
        }

        private void EnumerateDbProviders()
        {
            var dbProviders = _startupDbFactories
                ?? TypeLoader.GetImplementations<IDbFactory>(FsTools.GetBinaryDirectory(), "Diary.Db.*.dll").ToArray();
            foreach (var dbProvider in dbProviders)
            {
                Logger.LogInformation("Db provider: {Name}, Usable? {Usable}", dbProvider.Name, dbProvider.Usable);
                if (dbProvider.Usable)
                    _dbFactories.Add(dbProvider);
            }
        }

        public override IServiceProvider Services { get; protected set; }
        public override AllConfig AppConfig => AllConfig.Instance;

        public IReadOnlyDictionary<string, object> PluginConfigurations => _pluginConfigurations;

        public IReadOnlyList<ITrackerPlugin> Plugins => _plugins;

        public IDbFactory? GetDbFactory(string name)
            => _dbFactories.FirstOrDefault(factory => factory.Name == name);

        public ILogger Logger => Logging.Logger;

        public override IDbFactory? UseFactory { get; protected set; }
        public override DbInterfaceBase? UseDb { get; protected set; }

        private IServiceProvider ConfigureServices()
        {
            Logger.LogDebug("Configuring services");
            IServiceCollection services = new ServiceCollection();

            // mask add before
            services.AddSingleton(Logging.Logger);
            services.AddSingleton<ILoggerFactory>(Logging.Factory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton<BaseApp>(this);
            services.AddSingleton<PluginInstanceRegistry>();
            services.AddSingleton<TrackerInstanceCoordinator>();
            services.AddSingleton<TrackerPluginLifecycleCoordinator>();
            services.AddSingleton<TrackerPluginDiagnosticsService>();
            services.AddSingleton<TrackerUiContributionRegistry>();
            services.AddSingleton<IWorkItemPersistenceCoordinator, WorkItemPersistenceCoordinator>();
            services.AddSingleton<ITrackerUploadCoordinator, TrackerUploadCoordinator>();
            services.AddSingleton(_ => new CSharpEngine(
                Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts", "cache")));
            services.AddSingleton<LuaEngine>();
            services.AddSingleton<PythonRuntimeResolver>();
            services.AddSingleton<PythonEngine>(services => new PythonEngine(
                services.GetRequiredService<PythonRuntimeResolver>()));
            services.AddSingleton<IScriptEngineRegistry>(services =>
            {
                var registry = new ScriptEngineRegistry();
                registry.Register(services.GetRequiredService<CSharpEngine>());
                registry.Register(services.GetRequiredService<LuaEngine>());
                registry.Register(services.GetRequiredService<PythonEngine>());
                return registry;
            });
            services.AddSingleton<IScriptCatalog, ScriptCatalog>();
            services.AddSingleton<IScriptBuildService, ScriptBuildService>();
            services.AddSingleton<IScriptExecutor, ScriptExecutor>();
            services.AddSingleton<IWorkerHostCallDispatcher>(_ =>
                 new WorkItemQueryWorkerDispatcher(
                      () => new WorkItemQueryScriptApi(() => UseDb),
                      () => new TrackerInstanceScriptApi(
                          Services.GetRequiredService<PluginInstanceRegistry>()),
                      () => new LogItemScriptApi(() => UseDb),
                      () => new TemplateLogItemScriptApi(
                          () => UseDb,
                          () => TemplateManager.Instance.Templates.ToArray()),
                      () => new AppClipboardScriptApi(this),
                      () => new AppUserInteractionScriptApi()));
            services.AddSingleton<IWorkerScriptExecutor>(services =>
            {
                var workerName = OperatingSystem.IsWindows()
                    ? "Diary.Script.Worker.exe"
                    : "Diary.Script.Worker";
                var workerPath = Path.Combine(AppContext.BaseDirectory, workerName);
                var hostDispatcher = services.GetRequiredService<IWorkerHostCallDispatcher>();
                var csharpOptions = new WorkerProcessOptions(workerPath, [], AppContext.BaseDirectory);
                var luaOptions = new WorkerProcessOptions(workerPath, ["--language", "lua"], AppContext.BaseDirectory);
                var csharpRuntime = new WorkerRuntime(
                    "csharp",
                    new WorkerSupervisor(new ProcessWorkerTransportFactory(csharpOptions), hostDispatcher),
                     new WorkerHandshakeOptions("csharp", [ScriptApiVersion.V1], ["workItems.query", "logItems.create", "templateLogItems.create", "trackerInstances.get", "clipboard.get", "clipboard.set", "ui.notify", "ui.confirm"]));
                var luaRuntime = new WorkerRuntime(
                    "lua",
                    new WorkerSupervisor(new ProcessWorkerTransportFactory(luaOptions), hostDispatcher),
                     new WorkerHandshakeOptions("lua", [ScriptApiVersion.V1], ["workItems.query", "logItems.create", "templateLogItems.create", "trackerInstances.get", "clipboard.get", "clipboard.set", "ui.notify", "ui.confirm"]));
                var pythonRuntime = new WorkerRuntime(
                    "python",
                    new WorkerSupervisor(
                        new PythonWorkerTransportFactory(
                            services.GetRequiredService<PythonRuntimeResolver>()),
                        hostDispatcher,
                        maxRequestsPerWorker: 1),
                     new WorkerHandshakeOptions("python", [ScriptApiVersion.V1], ["workItems.query", "logItems.create", "templateLogItems.create", "trackerInstances.get", "clipboard.get", "clipboard.set", "ui.notify", "ui.confirm"]));
                return new WorkerScriptExecutor(
                    services.GetRequiredService<IScriptCatalog>(),
                    new Dictionary<string, WorkerRuntime>(StringComparer.OrdinalIgnoreCase)
                    {
                        [csharpRuntime.EngineName] = csharpRuntime,
                        [luaRuntime.EngineName] = luaRuntime,
                        [pythonRuntime.EngineName] = pythonRuntime,
                    });
            });
            services.AddSingleton<IScriptExecutionHistory, ScriptExecutionHistory>();
            services.AddSingleton<IScriptManager, ScriptManager>();
            services.AddSingleton<IScriptDirectoryLoader, ScriptDirectoryLoader>();
            services.AddSingleton<ScriptStartupDiagnosticsStore>();
            services.AddSingleton<IScriptExecutionContextFactory>(_ =>
                new ScriptExecutionContextFactory(metadata =>
                {
                    var context = new ScriptExecutionContext(metadata);
                    context.RegisterApi<IWorkItemQueryScriptApi>(
                        new WorkItemQueryScriptApi(() => UseDb));
                    context.RegisterApi<ITrackerInstanceScriptApi>(
                        new TrackerInstanceScriptApi(
                            Services.GetRequiredService<PluginInstanceRegistry>()));
                    context.RegisterApi<ILogItemScriptApi>(new LogItemScriptApi(() => UseDb));
                    context.RegisterApi<ITemplateLogItemScriptApi>(new TemplateLogItemScriptApi(
                        () => UseDb,
                        () => TemplateManager.Instance.Templates.ToArray()));
                    context.RegisterApi<IDiaryApi>(new DiaryApi(
                        context.GetApi<IWorkItemQueryScriptApi>()!,
                        context.GetApi<ILogItemScriptApi>()!,
                        context.GetApi<ITemplateLogItemScriptApi>()!));
                    context.RegisterApi<ITrackerApi>(new TrackerApi(context.GetApi<ITrackerInstanceScriptApi>()!));
                    context.RegisterApi<ISystemInteractionApi>(new SystemInteractionApi(
                        context.GetApi<IClipboardScriptApi>()!, context.GetApi<IUserInteractionScriptApi>()!));
                    return context;
                }));
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
            // 两阶段注册：先发现全部插件，建立已发现 ID 集，再做依赖存在性检查（§5.2）。
            var discovered = StartupOptions.CoreOnly
                ? new List<ITrackerPlugin>()
                : TypeLoader.GetImplementations<ITrackerPlugin>(
                    FsTools.GetBinaryDirectory(), "Diary.*.dll").ToList();
            var availablePlugins = discovered
                .GroupBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Manifest, StringComparer.Ordinal);
            compatibility = compatibility with
            {
                AvailablePluginIds = discovered.Select(p => p.Manifest.Id).ToHashSet(),
                AvailablePlugins = availablePlugins,
            };
            var cyclicPluginIds = PluginDependencyGraph.FindCyclicPluginIds(
                discovered.Select(plugin => plugin.Manifest));
            if (cyclicPluginIds.Count > 0)
            {
                Logger.LogError(
                    "插件依赖存在环，相关插件将被阻止：{PluginIds}",
                    string.Join(", ", cyclicPluginIds));
            }

            var pluginsById = discovered
                .GroupBy(plugin => plugin.Manifest.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var registeredPluginIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pluginId in PluginDependencyGraph.GetRegistrationOrder(
                         discovered.Select(plugin => plugin.Manifest)))
            {
                var plugin = pluginsById[pluginId];
                if (cyclicPluginIds.Contains(plugin.Manifest.Id))
                {
                    _pluginLoadDiagnostics[plugin.Manifest.Id] = new(
                        plugin,
                        new PluginLoadResult(PluginState.Blocked, "插件必选依赖存在环"));
                    Logger.LogError(
                        "Plugin {PluginId} blocked: dependency cycle",
                        plugin.Manifest.Id);
                    continue;
                }

                var missingRegisteredDependency = plugin.Manifest.Dependencies
                    .Where(dependency => !dependency.Optional)
                    .FirstOrDefault(dependency => !registeredPluginIds.Contains(dependency.PluginId));
                if (missingRegisteredDependency is not null
                    && availablePlugins.ContainsKey(missingRegisteredDependency.PluginId))
                {
                    _pluginLoadDiagnostics[plugin.Manifest.Id] = new(
                        plugin,
                        new PluginLoadResult(
                            PluginState.Blocked,
                            $"必选依赖未注册：{missingRegisteredDependency.PluginId}"));
                    Logger.LogError(
                        "Plugin {PluginId} blocked: required dependency {DependencyId} was not registered",
                        plugin.Manifest.Id,
                        missingRegisteredDependency.PluginId);
                    continue;
                }

                var result = PluginHost.Register(plugin, compatibility, services);
                _pluginLoadDiagnostics[plugin.Manifest.Id] = new(plugin, result);
                Logger.LogInformation("Plugin {PluginId}: {State}", plugin.Manifest.Id, result.State);
                if (result.State == PluginState.Compatible)
                {
                    try
                    {
                        var configuration = _pluginConfigurationLoader.Load(plugin);
                        _pluginConfigurations[plugin.Manifest.Id] = configuration;
                        _plugins.Add(plugin);
                        registeredPluginIds.Add(plugin.Manifest.Id);
                    }
                    catch (PluginConfigurationMigrationException ex)
                    {
                        _pluginLoadDiagnostics[plugin.Manifest.Id] = new(
                            plugin,
                            new PluginLoadResult(PluginState.ConfigurationMigrationFailed, ex.Message));
                        Logger.LogError(
                            ex,
                            "Plugin {PluginId} configuration migration failed at {FromVersion} -> {TargetVersion}",
                            plugin.Manifest.Id,
                            ex.FromVersion,
                            ex.TargetVersion);
                    }
                    catch (Exception ex)
                    {
                        _pluginLoadDiagnostics[plugin.Manifest.Id] = new(
                            plugin,
                            new PluginLoadResult(PluginState.Blocked, ex.Message));
                        Logger.LogError(ex, "Plugin {PluginId} configuration load failed", plugin.Manifest.Id);
                    }
                }
                if (result.State == PluginState.Blocked)
                    Logger.LogError("Plugin {PluginId} blocked: {Error}", plugin.Manifest.Id, result.Error);
            }
            services.AddTypesFromAssembly(Assembly.GetExecutingAssembly());
            services.AddTypesFromAssembly(typeof(ViewLocator).Assembly);
            if (!StartupOptions.CoreOnly)
                LoadPluginUiAssemblies(services);

            return services.BuildServiceProvider();
        }

        private void LoadPluginUiAssemblies(IServiceCollection services)
        {
            foreach (var path in Directory.EnumerateFiles(
                FsTools.GetBinaryDirectory(), "Diary.*.UI.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    services.AddTypesFromAssembly(Assembly.LoadFrom(path));
                    Logger.LogInformation("Plugin UI assembly loaded: {Path}", path);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Plugin UI assembly skipped: {Path}", path);
                }
            }
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
                if (success)
                    RegisterTrackerInstances();
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
                    if (UseDb is not null && DatabaseOk
                        && !string.Equals(_connectedDriver, AppConfig.DbSettings.DatabaseDriver, StringComparison.Ordinal))
                    {
                        SurveyEnabled = AppConfig.SurveySettings.Enabled;
                        return;
                    }
                    if (!ConfigureCheck(out var msg))
                    {
                        EventDispatcher.RouteToPage(PageNames.Settings);
                        EventDispatcher.Notify("错误", msg);
                        return;
                    }
                    RegisterTrackerInstances();
                    SurveyEnabled = AppConfig.SurveySettings.Enabled;
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
            SavePluginConfigurations();
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

        private void SavePluginConfigurations()
        {
            foreach (var plugin in _plugins)
            {
                if (!_pluginConfigurations.TryGetValue(plugin.Manifest.Id, out var configuration))
                    continue;

                if (!_pluginConfigurationLoader.Save(plugin, configuration))
                    Logger.LogWarning("退出时保存插件配置失败：{PluginId}", plugin.Manifest.Id);
            }
        }

        private void LoadConfigurations()
        {
            // EasySaveLoad.Load(AppConfig); // already loaded by instance
            SurveyEnabled = AppConfig.SurveySettings.Enabled;
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

    internal sealed class AppClipboardScriptApi(App app) : IClipboardScriptApi
    {
        public async ValueTask<string?> GetTextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clipboard = (app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
            return clipboard is null ? null : await clipboard.TryGetTextAsync();
        }

        public async ValueTask<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clipboard = (app.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;
            if (clipboard is null) return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
    }

    internal sealed class AppUserInteractionScriptApi : IUserInteractionScriptApi
    {
        public ValueTask NotifyAsync(string title, string body, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EventDispatcher.Notify(title, body);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<bool> ConfirmAsync(string title, string body, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await EventDispatcher.Confirm(title, body);
        }
    }
}
