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
using Diary.App.Fonts;
using Diary.App.Services;
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
using Diary.Update;
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
            _extendedSurveyor.ReceiveMessage += (_, s) =>
            {
                Dispatcher.UIThread.Post(() => EventDispatcher.Msg(new ExtendedRespondEvent(s)));
            };
            _extendedRespondent.ReceiveMessage += (_, s) =>
            {
                Dispatcher.UIThread.Post(() => EventDispatcher.Msg(new ExtendedSurveyRequestEvent(s)));
            };
        }

        public override void Initialize()
        {
            EnumerateDbProviders();
            LoadConfigurations();
            Services.GetRequiredService<TrackerPluginDiagnosticsService>().SetPluginStates(
                _pluginLoadDiagnostics.Values);

            ObserveBackgroundTask(LoadScriptsAsync(), "脚本目录加载");
            AvaloniaXamlLoader.Load(this);
            Services.GetRequiredService<AppFontService>().Apply(this, AppConfig.ViewSettings);
            DataContext = Services.GetRequiredService<AppModel>();

            // 调查响应处理不能依赖调查页是否显示；普通受访者也必须注册 v1/v2 请求处理器。
            _ = Services.GetRequiredService<SurveyViewModel>();

            // 同步主题设置
            SyncTheme();
            ObserveBackgroundTask(UpdateSurveyObjectsAsync(), "调查对象初始化");
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
                var scheduler = Services.GetRequiredService<ScriptAutomationScheduler>();
                scheduler.ApplyLoadResult(result);
                scheduler.Start();
                ObserveBackgroundTask(scheduler.RunStartupCatchUpAsync(), "启动自动化补跑");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "脚本目录加载失败");
            }
        }

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

        /// <summary>
        /// 重新尝试连接当前数据库配置，并在成功后恢复 Tracker 实例注册。
        /// </summary>
        public bool TryReconnectDatabase(out string message)
        {
            try
            {
                var success = ConfigureCheck(out message);
                DatabaseStatusMessage = success ? string.Empty : message;
                if (success)
                    RegisterTrackerInstances();
                return success;
            }
            catch (Exception ex)
            {
                message = "数据库打开异常，请检查配置或导出诊断日志。";
                DatabaseStatusMessage = message;
                Logger.LogError(ex, "重新连接数据库失败");
                return false;
            }
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

            var restoreCoordinator = Services.GetRequiredService<DatabaseRestoreCoordinator>();
            if (!restoreCoordinator.TryApplyPending(
                    factory.Name,
                    database as IDbMaintenanceProvider,
                    DataVersion.VersionCode,
                    out var pendingRestore,
                    out var restoreError))
            {
                database.Dispose();
                database = null;
                message = restoreError ?? "应用待还原数据库失败。";
                Logger.LogWarning("数据库待还原任务应用失败：驱动 {Driver}，原因 {Reason}",
                    factory.Name, message);
                return false;
            }

            try
            {
                // open
                Logger.LogInformation("开始连接数据库：驱动 {Driver}", factory.Name);
                if (!database.Connect())
                {
                    Logger.LogWarning("数据库连接失败：驱动 {Driver}", factory.Name);
                    return RejectDatabaseCandidate(
                        ref database, ref message, "数据库连接失败！", pendingRestore);
                }
                Logger.LogInformation("数据库连接成功：驱动 {Driver}", factory.Name);

                // 普通启动允许幂等初始化空库；待还原数据库必须先按备份原貌检查，
                // 避免 Initialized() 自动补表后掩盖不完整归档。
                if (pendingRestore is null && !database.Initialized())
                {
                    Logger.LogWarning("数据库初始化失败：驱动 {Driver}", factory.Name);
                    return RejectDatabaseCandidate(
                        ref database, ref message, "数据库初始化失败！", pendingRestore);
                }
                Logger.LogDebug(
                    pendingRestore is null
                        ? "数据库结构初始化成功：驱动 {Driver}"
                        : "待还原数据库跳过初始化并进入原貌兼容性检查：驱动 {Driver}",
                    factory.Name);

                // compatibility check: version is only one input; provider, schema, migration state and data integrity
                // are checked together before the application receives a writable database handle.
                var compatibility = database.CheckCompatibility(DataVersion.VersionCode);
                Logger.LogInformation(
                    "数据库兼容性检查完成：驱动 {Driver}，状态 {State}，声明版本 {DeclaredVersion}，目标版本 {ExpectedVersion}，结构指纹 {Fingerprint}",
                    factory.Name,
                    compatibility.State,
                    compatibility.DeclaredVersion,
                    compatibility.ExpectedVersion,
                    compatibility.ActualSchema.Fingerprint);
                foreach (var issue in compatibility.Issues.Where(issue => issue.Severity >= DbIssueSeverity.Warning))
                {
                    Logger.LogWarning(
                        "数据库兼容性问题：代码 {Code}，级别 {Severity}，对象 {ObjectName}，说明 {Message}",
                        issue.Code, issue.Severity, issue.ObjectName ?? "<database>", issue.Message);
                }

                if (compatibility.State == DbCompatibilityState.NeedsMigration)
                {
                    var migration = database.MigrateTo(DataVersion.VersionCode);
                    if (!migration.Success)
                    {
                        var migrationError = migration.Error ?? "数据库迁移失败，请检查诊断日志。";
                        Logger.LogWarning(
                            "数据库迁移失败：驱动 {Driver}，当前版本 {CurrentVersion}，目标版本 {TargetVersion}，原因 {Reason}",
                            factory.Name, migration.VersionFrom, DataVersion.VersionCode, migrationError);
                        return RejectDatabaseCandidate(
                            ref database, ref message, migrationError, pendingRestore);
                    }

                    compatibility = migration.FinalReport ?? database.CheckCompatibility(DataVersion.VersionCode);
                    Logger.LogInformation(
                        "数据库迁移完成并通过复检：驱动 {Driver}，版本 {Version}，备份 {BackupPath}",
                        factory.Name, compatibility.DeclaredVersion, migration.BackupPath ?? "<external>");
                }

                if (!compatibility.IsUsable)
                {
                    var compatibilityError = compatibility.ToUserMessage();
                    Logger.LogWarning(
                        "数据库不可用：驱动 {Driver}，状态 {State}，说明 {Message}",
                        factory.Name, compatibility.State, compatibilityError);
                    return RejectDatabaseCandidate(
                        ref database, ref message, compatibilityError, pendingRestore);
                }

                if (pendingRestore is not null && database is IDbPostRestoreValidator restoreValidator)
                {
                    var postRestoreValidation = restoreValidator.ValidateRestoredDatabase();
                    if (!postRestoreValidation.Success)
                    {
                        var validationError = postRestoreValidation.Error
                                              ?? "数据库还原后的附加完整性检查失败。";
                        Logger.LogWarning(
                            "数据库还原附加检查失败：驱动 {Driver}，原因 {Reason}",
                            factory.Name,
                            validationError);
                        return RejectDatabaseCandidate(
                            ref database,
                            ref message,
                            validationError,
                            pendingRestore);
                    }
                }

                if (!database.PersistCompatibilityMetadata(compatibility))
                {
                    Logger.LogWarning("数据库兼容性元数据写入失败：驱动 {Driver}", factory.Name);
                    return RejectDatabaseCandidate(
                        ref database,
                        ref message,
                        "数据库兼容性元数据写入失败，请检查数据库权限。",
                        pendingRestore);
                }

                if (pendingRestore is not null)
                {
                    // PostgreSQL 还原可能已经把配置切换到了新目标数据库；
                    // 只有启动兼容性检查和迁移复检全部通过后才持久化这次切换。
                    if (!string.IsNullOrWhiteSpace(pendingRestore.RestoreResult.PreviousDatabase)
                        && !EasySaveLoad.Save(dbConfig))
                    {
                        return RejectDatabaseCandidate(
                            ref database,
                            ref message,
                            "数据库还原已通过检查，但无法保存新的数据库配置。",
                            pendingRestore);
                    }

                    restoreCoordinator.Complete(pendingRestore);
                    Logger.LogInformation("数据库还原完成并通过启动复检：驱动 {Driver}", factory.Name);
                }
                UseFactory = factory;
                _connectedDriver = factory.Name;
                Logger.LogInformation("数据库连接候选已验证：驱动 {Driver}", _connectedDriver);
                return true;
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "数据库连接候选验证异常：驱动 {Driver}", factory.Name);
                return RejectDatabaseCandidate(
                    ref database,
                    ref message,
                    "数据库打开异常，请检查配置或诊断日志。",
                    pendingRestore);
            }
        }

        private bool RejectDatabaseCandidate(
            ref DbInterfaceBase? database,
            ref string message,
            string failureMessage,
            PendingDatabaseRestoreContext? pendingRestore)
        {
            try
            {
                database?.Dispose();
            }
            catch (Exception exception)
            {
                Logger.LogWarning(exception, "关闭失败的数据库连接候选时发生异常");
            }
            database = null;
            message = failureMessage;

            if (pendingRestore is null)
                return false;

            var coordinator = Services.GetRequiredService<DatabaseRestoreCoordinator>();
            if (!coordinator.Rollback(pendingRestore, out var rollbackError))
            {
                message += $" 自动恢复还原前数据库失败：{rollbackError}";
                Logger.LogError("数据库还原后的启动检查失败，且自动回滚失败：{Reason}", rollbackError);
            }
            else
            {
                Logger.LogWarning("数据库还原后的启动检查失败，已恢复还原前数据库");
            }
            return false;
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
            services.AddSingleton<DatabaseRestoreCoordinator>();
            services.AddSingleton(_ =>
            {
                var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DiaryApp-UpdateClient/1");
                return client;
            });
            services.AddSingleton<IUpdateSource, HttpUpdateSource>();
            services.AddSingleton<UpdateChecker>();
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
            services.AddSingleton<IScriptIdempotencyStore>(_ => new ScriptIdempotencyStore(
                Path.Combine(FsTools.GetApplicationConfigDirectory(), "scripts", "idempotency.json")));
            services.AddSingleton<IWorkerHostCallDispatcher>(_ =>
                 new WorkItemQueryWorkerDispatcher(
                      () => new WorkItemQueryScriptApi(() => UseDb),
                     () => new TrackerInstanceScriptApi(
                         Services.GetRequiredService<PluginInstanceRegistry>()),
                     () => new LogItemScriptApi(
                         () => UseDb,
                         Services.GetRequiredService<IScriptIdempotencyStore>(),
                         () => EventDispatcher.DbChanged(DbChangedEvent.ShareData)),
                      () => new TemplateLogItemScriptApi(
                          () => UseDb,
                          () => TemplateManager.Instance.Templates.ToArray(),
                          Services.GetRequiredService<IScriptIdempotencyStore>(),
                          () => EventDispatcher.DbChanged(DbChangedEvent.ShareData)),
                       () => new TemplateScriptApi(
                           () => TemplateManager.Instance.Templates.ToArray()),
                      () => new HostCapabilitiesScriptApi(() => ScriptHostApiCatalog.All),
                      () => new AppClipboardScriptApi(this),
                      () => new AppUserInteractionScriptApi(),
                      fileInteractionApiFactory: context => new AppFileInteractionScriptApi(
                          this,
                          Services.GetRequiredService<ScriptExportService>(),
                          context),
                      exportApiFactory: _ => Services.GetRequiredService<ScriptExportService>(),
                      scriptLogApiFactory: executionId => CreateScriptLogApi(null, executionId),
                      (executionId, update, _) =>
                      {
                          Services.GetRequiredService<ScriptProgressTracker>().Report(executionId, update);
                          return ValueTask.CompletedTask;
                      }));
            services.AddSingleton<IWorkerScriptExecutor>(services =>
            {
                var workerName = OperatingSystem.IsWindows()
                    ? "Diary.Script.Worker.exe"
                    : "Diary.Script.Worker";
                var workerPath = Path.Combine(AppContext.BaseDirectory, workerName);
                var hostDispatcher = services.GetRequiredService<IWorkerHostCallDispatcher>();
                var csharpOptions = new WorkerProcessOptions(workerPath, [], AppContext.BaseDirectory);
                var luaOptions = new WorkerProcessOptions(workerPath, ["--language", "lua"], AppContext.BaseDirectory);
                var sharedWorkerPolicy = WorkerRuntimePolicy.Shared;
                var dedicatedWorkerPolicy = WorkerRuntimePolicy.Dedicated;
                var csharpRuntime = new WorkerRuntime(
                    "csharp",
                    new WorkerSupervisor(
                        new ProcessWorkerTransportFactory(csharpOptions),
                        hostDispatcher,
                        maxRequestsPerWorker: sharedWorkerPolicy.MaxRequestsPerWorker,
                        handshakeTimeout: TimeSpan.FromSeconds(10),
                        hostCallTimeout: TimeSpan.FromSeconds(30),
                        heartbeatInterval: TimeSpan.FromSeconds(30),
                        heartbeatTimeout: TimeSpan.FromSeconds(15)),
                    new WorkerHandshakeOptions("csharp", [ScriptApiVersion.V1], ScriptHostApiCatalog.All),
                    sharedWorkerPolicy);
                var luaRuntime = new WorkerRuntime(
                    "lua",
                    new WorkerSupervisor(
                        new ProcessWorkerTransportFactory(luaOptions),
                        hostDispatcher,
                        maxRequestsPerWorker: sharedWorkerPolicy.MaxRequestsPerWorker,
                        handshakeTimeout: TimeSpan.FromSeconds(10),
                        hostCallTimeout: TimeSpan.FromSeconds(30),
                        heartbeatInterval: TimeSpan.FromSeconds(30),
                        heartbeatTimeout: TimeSpan.FromSeconds(15)),
                    new WorkerHandshakeOptions("lua", [ScriptApiVersion.V1], ScriptHostApiCatalog.All),
                    sharedWorkerPolicy);
                var pythonRuntime = new WorkerRuntime(
                    "python",
                    new WorkerSupervisor(
                        new PythonWorkerTransportFactory(
                            services.GetRequiredService<PythonRuntimeResolver>()),
                        hostDispatcher,
                        maxRequestsPerWorker: dedicatedWorkerPolicy.MaxRequestsPerWorker,
                        handshakeTimeout: TimeSpan.FromSeconds(10),
                        hostCallTimeout: TimeSpan.FromSeconds(30),
                        heartbeatInterval: TimeSpan.FromSeconds(30),
                        heartbeatTimeout: TimeSpan.FromSeconds(15)),
                    new WorkerHandshakeOptions("python", [ScriptApiVersion.V1], ScriptHostApiCatalog.All),
                    dedicatedWorkerPolicy);
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
            services.AddSingleton<ScriptLogStore>();
            services.AddSingleton<ScriptProgressTracker>();
            var exportPluginDirectory = FsTools.GetBinaryDirectory();
            const string exportPluginPattern = "Diary.Export.*.dll";
            var exportPluginFiles = Directory.GetFiles(
                    exportPluginDirectory,
                    exportPluginPattern,
                    SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Logger.LogInformation(
                "开始扫描导出插件：目录 {Directory}，模式 {Pattern}，候选程序集 {CandidateCount} 个",
                exportPluginDirectory,
                exportPluginPattern,
                exportPluginFiles.Length);

            var exportPluginCount = 0;
            var exportHandlerCount = 0;
            var exportTemplateHandlerCount = 0;
            foreach (var exportPluginPath in exportPluginFiles)
            {
                var assemblyFileName = Path.GetFileName(exportPluginPath);
                Logger.LogDebug("正在加载导出插件程序集：{AssemblyFile}", assemblyFileName);
                Exception? loadException = null;
                var exportPlugin = TypeLoader.LoadAssemblyAndGetInstance<IExportPlugin>(
                    exportPluginPath,
                    exception =>
                    {
                        loadException = exception;
                        Logger.LogError(
                            exception,
                            "导出插件程序集加载失败：{AssemblyFile}",
                            assemblyFileName);
                    });
                if (exportPlugin is null)
                {
                    if (loadException is null)
                        Logger.LogWarning(
                            "导出插件程序集未发现可实例化的 IExportPlugin：{AssemblyFile}",
                            assemblyFileName);
                    continue;
                }

                IExportHandler[] exportHandlers;
                IExportTemplateHandler[] exportTemplateHandlers;
                try
                {
                    exportHandlers = exportPlugin.GetExportHandlers().ToArray();
                    exportTemplateHandlers = exportPlugin.GetTemplateHandlers().ToArray();
                }
                catch (Exception exception)
                {
                    Logger.LogError(
                        exception,
                        "读取导出插件处理器失败：插件 {PluginId}，程序集 {AssemblyFile}",
                        exportPlugin.Manifest.Id,
                        assemblyFileName);
                    throw;
                }

                services.AddSingleton(exportPlugin);
                foreach (var handler in exportHandlers)
                    services.AddSingleton<IExportHandler>(handler);
                foreach (var handler in exportTemplateHandlers)
                    services.AddSingleton<IExportTemplateHandler>(handler);
                exportPluginCount++;
                exportHandlerCount += exportHandlers.Length;
                exportTemplateHandlerCount += exportTemplateHandlers.Length;
                Logger.LogInformation(
                    "导出插件加载成功：{PluginId} v{PluginVersion}，程序集 {AssemblyFile}，格式 [{FormatIds}]，模板扩展名 [{TemplateExtensions}]",
                    exportPlugin.Manifest.Id,
                    exportPlugin.Manifest.Version,
                    assemblyFileName,
                    string.Join(", ", exportHandlers.Select(handler => handler.Descriptor.FormatId)),
                    string.Join(", ", exportTemplateHandlers.SelectMany(handler => handler.SupportedTemplateExtensions).Distinct(StringComparer.OrdinalIgnoreCase)));
            }
            Logger.LogInformation(
                "导出插件扫描完成：插件 {PluginCount} 个，格式处理器 {ExportHandlerCount} 个，模板处理器 {TemplateHandlerCount} 个",
                exportPluginCount,
                exportHandlerCount,
                exportTemplateHandlerCount);
            services.AddSingleton<ExportTemplateCatalog>();
            services.AddSingleton<IExportTemplateCatalog>(services =>
                services.GetRequiredService<ExportTemplateCatalog>());
            services.AddSingleton<ScriptExportService>();
            services.AddSingleton<IScriptExecutionContextFactory>(_ =>
                new ScriptExecutionContextFactory((metadata, request) =>
                {
                    IWorkItemQueryScriptApi queryApi = new WorkItemQueryScriptApi(() => UseDb);
                    var context = new ScriptExecutionContext(
                        metadata,
                        request.Target,
                        request.Arguments,
                        (range, cancellationToken) => queryApi.StreamAsync(new ScriptWorkItemQuery
                        {
                            StartDate = range.StartDate,
                            EndDate = range.EndDate,
                        }, cancellationToken: cancellationToken),
                        progressReporter: update =>
                        {
                            Services.GetRequiredService<ScriptProgressTracker>().Report(
                                metadata.ExecutionId.ToString(), update);
                            return ValueTask.CompletedTask;
                        });
                    var hostContext = new ScriptHostCallContext(
                        metadata.ExecutionId.ToString(),
                        "in-process",
                        metadata.ScriptId,
                        metadata.EntryKind,
                        metadata.Source,
                        metadata.Preview);
                    context.RegisterApi<IWorkItemQueryScriptApi>(queryApi);
                    context.RegisterApi<ILogApi>(CreateScriptLogApi(metadata, null));
                    context.RegisterApi<ITrackerInstanceScriptApi>(
                         new TrackerInstanceScriptApi(
                             Services.GetRequiredService<PluginInstanceRegistry>()));
                    context.RegisterApi<ILogItemScriptApi>(new ExecutionPolicyLogItemScriptApi(
                        new LogItemScriptApi(
                            () => UseDb,
                            Services.GetRequiredService<IScriptIdempotencyStore>(),
                            () => EventDispatcher.DbChanged(DbChangedEvent.ShareData)),
                        hostContext));
                    context.RegisterApi<ITemplateLogItemScriptApi>(new ExecutionPolicyTemplateLogItemScriptApi(
                        new TemplateLogItemScriptApi(
                            () => UseDb,
                            () => TemplateManager.Instance.Templates.ToArray(),
                            Services.GetRequiredService<IScriptIdempotencyStore>(),
                            () => EventDispatcher.DbChanged(DbChangedEvent.ShareData)),
                        hostContext));
                    context.RegisterApi<ITemplateScriptApi>(new TemplateScriptApi(
                         () => TemplateManager.Instance.Templates.ToArray()));
                    context.RegisterApi<IHostCapabilitiesScriptApi>(new HostCapabilitiesScriptApi(
                        () => ScriptHostApiCatalog.All));
                    context.RegisterApi<IClipboardScriptApi>(new ExecutionPolicyClipboardScriptApi(
                        new AppClipboardScriptApi(this),
                        hostContext));
                    context.RegisterApi<IUserInteractionScriptApi>(new AppUserInteractionScriptApi());
                    context.RegisterApi<IFileInteractionApi>(new AppFileInteractionScriptApi(
                        this, Services.GetRequiredService<ScriptExportService>(), hostContext));
                    context.RegisterApi<IExportApi>(new ContextualExportScriptApi(
                        Services.GetRequiredService<ScriptExportService>(), hostContext));
                    context.RegisterApi<IDiaryApi>(new DiaryApi(
                         context.GetApi<IWorkItemQueryScriptApi>()!,
                         context.GetApi<ILogItemScriptApi>()!,
                         context.GetApi<ITemplateLogItemScriptApi>()!,
                          context.GetApi<ITemplateScriptApi>()!,
                          context.GetApi<IHostCapabilitiesScriptApi>()!));
                    context.RegisterApi<ITrackerApi>(new TrackerApi(context.GetApi<ITrackerInstanceScriptApi>()!));
                    context.RegisterApi<SysApi>(new SystemInteractionApi(
                       context.GetApi<IClipboardScriptApi>()!,
                       context.GetApi<IUserInteractionScriptApi>()!,
                       context.GetApi<IFileInteractionApi>()!));
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
            DatabaseStatusMessage = success ? string.Empty : message;

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
                // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
                DisableAvaloniaDataAnnotationValidation();
                desktop.MainWindow = new MainWindow();
                var vm = Services.GetRequiredService<MainWindowViewModel>();
                vm.SetView(desktop.MainWindow);
                desktop.MainWindow.DataContext = vm;
                desktop.ShutdownRequested += OnShutdownRequested;
            }

            base.OnFrameworkInitializationCompleted();

            WeakReferenceMessenger.Default.Register<ConfigUpdateEvent>(this, (r, m) =>
            {
                Dispatcher.UIThread.Post(() => ObserveBackgroundTask(
                    HandleConfigUpdateAsync(),
                    "配置更新后的调查对象重载"));
            });
            WeakReferenceMessenger.Default.Register<SurveyResultEvent>(this, (r, m) =>
            {
                _respondent.Send(m.Value);
            });
            WeakReferenceMessenger.Default.Register<SurveyQueryEvent>(this, (r, m) =>
            {
                ObserveBackgroundTask(_surveyor.SurveyAsync(m.Value), "发送调查问题");
            });
            WeakReferenceMessenger.Default.Register<ExtendedSurveyResultEvent>(this, (r, m) =>
            {
                _extendedRespondent.Send(m.Value);
            });
            WeakReferenceMessenger.Default.Register<ExtendedSurveyQueryEvent>(this, (r, m) =>
            {
                ObserveBackgroundTask(_extendedSurveyor.SurveyAsync(m.Value), "发送扩展调查问题");
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

        private async Task HandleConfigUpdateAsync()
        {
            if (UseDb is not null && DatabaseOk
                && !string.Equals(_connectedDriver, AppConfig.DbSettings.DatabaseDriver, StringComparison.Ordinal))
            {
                SurveyEnabled = AppConfig.SurveySettings.Enabled;
                return;
            }

            if (!ConfigureCheck(out var msg))
            {
                DatabaseStatusMessage = msg;
                EventDispatcher.RouteToPage(PageNames.Settings);
                EventDispatcher.Notify("错误", msg);
                return;
            }

            DatabaseStatusMessage = string.Empty;
            RegisterTrackerInstances();
            SurveyEnabled = AppConfig.SurveySettings.Enabled;
            await UpdateSurveyObjectsAsync();
        }

        private async Task UpdateSurveyObjectsAsync()
        {
            if (Design.IsDesignMode)
                return;

            await _surveyor.StopServerAsync();
            await _respondent.ShutdownAsync();
            await _extendedSurveyor.StopServerAsync();
            await _extendedRespondent.ShutdownAsync();

            if (!AppConfig.SurveySettings.Enabled)
                return;

            var surveyConfig = AppConfig.SurveySettings;
            if (surveyConfig.IsServerEnabled)
            {
                _surveyor.StartServer();
                _extendedSurveyor.StartServer();
            }

            if (surveyConfig.TryGetRespondentAddress(out var address))
            {
                _respondent.Connect(address);
                _extendedRespondent.Connect(address);
            }
            else if (surveyConfig.IsRespondentEnabled)
                Logger.LogWarning("调查功能已启用但未配置调查者 IP 地址，受访者不会连接调查者");
        }

        private async Task PreShutdownAsync()
        {
            await _surveyor.StopServerAsync();
            await _respondent.ShutdownAsync();
            await _extendedSurveyor.StopServerAsync();
            await _extendedRespondent.ShutdownAsync();
            _timer.Stop();
            (Services.GetRequiredService<ScriptAutomationScheduler>() as IDisposable)?.Dispose();
            await Services.GetRequiredService<IWorkerScriptExecutor>().StopAllAsync();
            SaveConfigurations();
            SavePluginConfigurations();
            (Services as IDisposable)?.Dispose();
            Logging.Shutdown();
        }

        private Task? _preShutdownTask;
        private bool _shutdownCompleted;

        private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
        {
            if (_shutdownCompleted)
                return;

            e.Cancel = true;
            _preShutdownTask ??= PreShutdownAsync();
            try
            {
                await _preShutdownTask;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "应用退出清理失败");
                return;
            }

            _shutdownCompleted = true;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }

        private void ObserveBackgroundTask(Task task, string operation)
        {
            _ = task.ContinueWith(
                completedTask => Logger.LogError(completedTask.Exception, "{Operation}失败", operation),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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
                "APP_FONT" => new SettingFont(caption, helpTip, obj),
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

        private static readonly DirectProperty<App, string> DatabaseStatusMessageProperty =
            AvaloniaProperty.RegisterDirect<App, string>(nameof(DatabaseStatusMessage), app => app.DatabaseStatusMessage);
        private string _databaseStatusMessage = "数据库尚未连接，请检查数据库设置。";
        public override string DatabaseStatusMessage
        {
            get => _databaseStatusMessage;
            protected set => SetAndRaise(DatabaseStatusMessageProperty, ref _databaseStatusMessage, value);
        }

        private static readonly StyledProperty<bool> SurveyEnabledProperty = AvaloniaProperty.Register<App, bool>(nameof(SurveyEnabled), false);
        public override bool SurveyEnabled
        {
            get => GetValue(SurveyEnabledProperty);
            protected set => SetValue(SurveyEnabledProperty, value);
        }

        private AppSurveyor _surveyor = new();
        private AppRespondent _respondent = new();
        private AppSurveyor _extendedSurveyor = new(SurveyPorts.Extended);
        private AppRespondent _extendedRespondent = new(SurveyPorts.Extended);

        private ScriptLogApi CreateScriptLogApi(
            ScriptExecutionMetadata? metadata,
            string? executionId)
        {
            var scriptId = metadata?.ScriptId ?? "worker";
            var correlationId = metadata?.ExecutionId.ToString("N") ?? executionId ?? "unknown";
            var store = Services.GetRequiredService<ScriptLogStore>();
            return new ScriptLogApi((level, message) =>
            {
                var formatted = $"[Script:{scriptId}][Execution:{correlationId}] {message}";
                store.Append(level, formatted);
                switch (level)
                {
                    case ScriptLogLevel.Debug:
                        Logging.Logger.LogDebug("{ScriptMessage}", formatted);
                        break;
                    case ScriptLogLevel.Info:
                        Logging.Logger.LogInformation("{ScriptMessage}", formatted);
                        break;
                    case ScriptLogLevel.Warning:
                        Logging.Logger.LogWarning("{ScriptMessage}", formatted);
                        break;
                    case ScriptLogLevel.Error:
                        Logging.Logger.LogError("{ScriptMessage}", formatted);
                        break;
                }
            });
        }
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
